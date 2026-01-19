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
#   <full path to the program under test, e.g., $SCRIPT_DIR/../DafnyBench/DafnyBench/dataset/ground_truth/630-dafny_tmp_tmpz2kokaiq_Solution.dfy> 
#   [help]
# ------------------------------------------------------------------------------ General utils

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" > /dev/null 2>&1 && pwd)"

die() {
  echo "$@" >&2
  exit 1
}

# ------------------------------------------------------------------------------ Args e vars

USAGE="Usage: ${BASH_SOURCE[0]}
   <full path to the program under test, e.g., $SCRIPT_DIR/../DafnyBench/DafnyBench/dataset/ground_truth/630-dafny_tmp_tmpz2kokaiq_Solution.dfy>
   [help]"
if [ "$#" -ne "1" ]; then
  die "$USAGE"
fi

if [ "$1" = "--help" ]; then
    echo "$USAGE"
    exit 0
fi

PROGRAM=$1
PLUGIN="$SCRIPT_DIR/snapshot-generator/bin/Debug/net8.0/SnapshotGenerator.dll"

# ------------------------------------------------------------------------------ Utils

verify_program() {
    echo "Attempting to verify $PROGRAM"
    dotnet ./dafny/Binaries/Dafny.dll verify "$PROGRAM"
}

infer_invariants() {
    local passing="$1"
    local violation_line="$2"
    local violation_col="$3"

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
    dotnet ./dafny/Binaries/Dafny.dll run "$PROGRAM" \
        --plugin $PLUGIN,"$violation_line $violation_col $inv_type_arg" \
        --no-verify --allow-warnings > "$trace_output_file"

    sed -i '' '1,2d' "$trace_output_file" # remove output relative to verification
    java -cp $DAIKONDIR/daikon.jar daikon.Daikon "$trace_output_file" --format Simplify > "$inv_output_file"
}

generate_snapshots() {
    local enumeration="$1"
    local violation_line="$2"
    local violation_col="$3"

    enumeration_str=""
    gen_type_arg=""
    snapshot_output_file=""
    if [[ $enumeration == true ]]; then 
        enumeration_str="enumeration" 
        gen_type_arg="enum"
        snapshot_output_file="snapshots-enum.txt"
    else 
        enumeration_str="invariants"
        gen_type_arg="inv"
        snapshot_output_file="snapshots-inv.txt"
    fi

    echo "Generating snapshots via $enumeration_str"
    dotnet ./dafny/Binaries/Dafny.dll run "$PROGRAM" \
        --plugin $PLUGIN,"$violation_line $violation_col $gen_type_arg" \
        --no-verify --allow-warnings > "$snapshot_output_file"
    sed -i '' '1,2d' "$snapshot_output_file" # remove output relative to verification
}

# ------------------------------------------------------------------------------ Main

# Get Dafny verification output

output="$(verify_program)"
verified=$(echo $output | grep "Dafny program verifier finished.*0 errors")
if [[ $verified ]]; then
    echo "Program verifies, no fault detected"
    exit 0
else
    echo "Program does not verify"
fi

violation=$(echo "$output" | grep "Error: a precondition\|Error: a postcondition")
echo -e "$violation\n"
violation_location=$(echo "$violation" | grep -o "(.*,.*)")
violation_line=$(echo $violation_location | grep -o '[0-9]*' | awk 'NR==1')
violation_col=$(echo $violation_location | grep -o '[0-9]*' | awk 'NR==2')

# Generate passing and failing invariants

echo "$(infer_invariants true $violation_line $violation_col)"
echo -e "$(infer_invariants false $violation_line $violation_col)\n"
# Filter invariants: we are interested in failing invariants that are not passing
python3 filter-invs.py

# Generate snapshots via enumeration and invariants

echo "$(generate_snapshots true $violation_line $violation_col)"
echo "$(generate_snapshots false $violation_line $violation_col)"