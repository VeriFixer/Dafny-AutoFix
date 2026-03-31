#!/usr/bin/env bash
#
# ------------------------------------------------------------------------------
# Givem a Dafny program that does not verify, this script generates a set of 
# snapshots (l, p, v) abstracting its state and computes their suspiciousness score, 
# indicating the  probability of the fault present in the program being due to 
# predicate p taking value v at location l
# 
#
# Usage:
# run.sh
#   <full path to the program under test, e.g., $SCRIPT_DIR/../datasets/630-dafny_tmp_tmpz2kokaiq_Solution.dfy> 
#   --strategy <suspiciousness score formula to use, i.e., dynamic-and-static-score or dynamic-score-only>
#   [help]
# ------------------------------------------------------------------------------ General utils
find_repo_root() {
    local marker="${1:-.repo_verifixer_fault_localization_marker}"
    local dir

    dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"

    while [[ "$dir" != "/" ]]; do
        if [[ -e "$dir/$marker" ]]; then
            echo "$dir"
            return 0
        fi
        dir="$(dirname "$dir")"
    done

    echo "Error: Could not find repository root (marker: $marker)" >&2
    return 1
}
REPO_ROOT=$(find_repo_root)

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" > /dev/null 2>&1 && pwd)"

die() {
  echo "$@" >&2
  exit 1
}

# ------------------------------------------------------------------------------ Args e vars

USAGE="Usage: ${BASH_SOURCE[0]}
   <full path to the program under test, e.g., $SCRIPT_DIR/../datasets/630-dafny_tmp_tmpz2kokaiq_Solution.dfy>
   --strategy <suspiciousness score formula to use, e.g., dynamic-and-static-score or dynamic-score-only>
   --out-dir <output_directory, e.g, /app/run_artifacts/autofix_fl_run>
   [help]"
if [ "$#" -ne "1" ] && [ "$#" -ne "3" ] && [ "$#" -ne "5" ]; then
  die "$USAGE"
fi

PROGRAM=""
STRATEGY=""
OUT_DIR="$(pwd)"

if [ "$#" -eq "1" ] && [ "$1" = "--help" ]; then
    echo "$USAGE"
    exit 0
elif [ "$#" -eq "3" ] && [ "$2" = "--strategy" ]; then
    PROGRAM=$1
    STRATEGY=$3
elif [ "$#" -eq "5" ]; then
    PROGRAM=$1
    if [ "$2" = "--strategy" ] && [ "$4" = "--out-dir" ]; then
        STRATEGY=$3
        OUT_DIR=$5
    elif [ "$4" = "--strategy" ] && [ "$2" = "--out-dir" ]; then
        STRATEGY=$5
        OUT_DIR=$3
    else
        die "$USAGE"
    fi
else
    die "$USAGE"
fi

# Check whether all arguments have been initialized
[ "$PROGRAM" != "" ]  || die "[ERROR] Missing program file argument!"
[ "$STRATEGY" != "" ] || die "[ERROR] Missing --strategy argument!"
[ "$STRATEGY" = "dynamic-and-static-score" ] || [ "$STRATEGY" = "dynamic-score-only" ] || die "[ERROR] Strategy should be dynamic-and-static-score or dynamic-score-only!"

PLUGIN="$REPO_ROOT/build_output/Autofix/AutoFix.dll"

# ------------------------------------------------------------------------------ Utils

verify_program() {
    echo "Attempting to verify $PROGRAM"
    dafny verify "$PROGRAM" 
}

infer_invariants() {
    local passing="$1"
    local violation_line="$2"

    passing_str=""
    inv_type_arg=""
    trace_output_file=""
    inv_output_file=""
    if [[ $passing == true ]]; then 
        passing_str="passing" 
        inv_type_arg="inv_pass"
        trace_output_file="trace-pass.dtrace"
        inv_output_file="inv-pass.inv"
    else 
        passing_str="failing"
        inv_type_arg="inv_fail"
        trace_output_file="trace-fail.dtrace"
        inv_output_file="inv-fail.inv"
    fi

    echo "Generating invariants for $passing_str tests"
    dafny run "$PROGRAM" \
        --plugin $PLUGIN,"$inv_type_arg $violation_line" \
        --no-verify --allow-warnings \
         > "$trace_output_file"

    sed -n '/^decl-version 2\.0/,$p' "$trace_output_file" > "${trace_output_file}.tmp"
    mv "${trace_output_file}.tmp" "$trace_output_file"
    java -cp $DAIKONDIR/daikon.jar daikon.Daikon "$trace_output_file" --format Simplify > "$inv_output_file"
}

generate_snapshots() {
    local violation_line="$1"
    local related_location_line="$2"

    echo "Generating snapshots"
    dafny run "$PROGRAM" \
        --plugin $PLUGIN,"snap $violation_line $related_location_line" \
        --no-verify --allow-warnings \
         > snapshots.csv
    sed '1,2d' snapshots.csv > snapshots.tmp 
    mv snapshots.tmp snapshots.csv
}

# ------------------------------------------------------------------------------ Main

pushd . > /dev/null 2>&1
cd "$OUT_DIR"

# Get Dafny verification output
output="$(verify_program)"
verified=$(echo $output | grep "Dafny program verifier finished.*0 errors")
if [[ $verified ]]; then
    echo "Program verifies, no fault detected"
    exit 0
else
    echo "Program does not verify"
fi

violation=$(echo "$output" | grep "Error: a precondition\|Error: a postcondition\|Error: this loop invariant\|Error: assertion")
if [ ! "$str" ];then
   violation=$(echo "$output" | grep "Error: ")
fi
echo -e "$violation\n"
violation_location=$(echo "$violation" | grep -o "(.*,.*)")
violation_line=$(echo $violation_location | grep -o '[0-9]*' | awk 'NR==1')
related_location=$(echo "$output" | grep "Related location:")
echo -e "$related_location\n"
related_location=$(echo "$related_location" | grep -o "(.*,.*)")
related_location_line=$(echo $related_location | grep -o '[0-9]*' | awk 'NR==1')

# Generate passing and failing invariants

echo "$(infer_invariants true $violation_line)"
echo -e "$(infer_invariants false $violation_line)\n"
# Filter invariants: we are interested in failing invariants that are not passing
python3 "$SCRIPT_DIR/filter-invs.py"

# Generate snapshots via enumeration and invariants
echo -e "$(generate_snapshots $violation_line $related_location_line)\n"

# Compute the suspiciousness score of each snapshot
echo "Computing suspiciousness scores"
if [ "$STRATEGY" = "dynamic-and-static-score" ]; then
    python3 "$SCRIPT_DIR/compute-suspiciousness.py"
else
    python3 "$SCRIPT_DIR/compute-suspiciousness.py short-score"
fi

popd > /dev/null 2>&1