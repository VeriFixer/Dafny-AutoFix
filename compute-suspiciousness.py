import sys

def read_snapshots():
    enum_snapshots = set()
    inv_snapshots = set()
    pass_snapshots = {}
    fail_snapshots = {}
    has_passing_tests = False
    has_failing_tests = False

    with open("snapshots.csv", 'r') as file:
        passing = None
        current_test_case_snapshots = set()
        for line in file:
            if line.strip() == "Running passing tests":
                passing = True
            elif line.strip() == "Running failing tests":
                passing = False
            elif line.strip() == "Running test case":
                current_test_case_snapshots = set()
                if passing:
                    has_passing_tests = True
                elif passing != None:
                    has_failing_tests = True
            elif passing != None:
                full_snapshot = tuple(line.strip().split(';'))
                snapshot = full_snapshot[:6]
                collection = enum_snapshots if full_snapshot[6] == "enum" else inv_snapshots
                collection.add(snapshot)
                collection = pass_snapshots if passing else fail_snapshots
                if snapshot in current_test_case_snapshots:
                    continue
                current_test_case_snapshots.add(snapshot)
                if snapshot in collection:
                    collection[snapshot] += 1
                else:
                    collection[snapshot] = 1

    return (enum_snapshots, inv_snapshots, pass_snapshots, fail_snapshots, has_passing_tests, has_failing_tests)   


def compute_scores(use_complete_score, pass_snapshots, fail_snapshots):
    scores = {}

    for snapshot in set(pass_snapshots) | set(fail_snapshots):
        control_dependence_score = float(snapshot[4]) if float(snapshot[4]) != 0 else 0.00001
        expression_dependence_score = float(snapshot[5]) if float(snapshot[5]) != 0 else 0.00001
        alpha = 1 / 3
        beta = 2 / 3
        gamma = 1
        num_pass = pass_snapshots.get(snapshot, 0)
        num_fail = fail_snapshots.get(snapshot, 0)
        dynamic_score = gamma + (alpha / (1 - alpha)) * (1 - beta + (beta * alpha ** num_pass) - (alpha ** num_fail))
        score = 3 / (control_dependence_score ** -1 + expression_dependence_score ** -1 + dynamic_score ** -1)
        scores[snapshot] = round(score if use_complete_score else dynamic_score, 5)
    
    ordered_scores = {k: v for k, v in sorted(scores.items(), key=lambda item: (-item[1], int(item[0][0])))}
    return ordered_scores


def main():
    use_complete_score = True
    if len(sys.argv) > 1 and sys.argv[1] == "short-score":
        use_complete_score = False

    (enum_snapshots, inv_snapshots, pass_snapshots, fail_snapshots, has_passing, has_failing) = read_snapshots()
    snapshot_scores = compute_scores(use_complete_score, pass_snapshots, fail_snapshots)

    with open("snapshots-suspiciousness-score.csv", "w") as f:
        for snapshot, score in snapshot_scores.items():
            source = "both" if snapshot in enum_snapshots and snapshot in inv_snapshots \
                else "enum" if snapshot in enum_snapshots else "inv"
            f.write(f"{snapshot[:4]},{score},{source}\n")
        f.close()
    with open("lines-suspiciousness.csv", "w") as f:
        for snapshot, score in snapshot_scores.items():
            f.write(f"{snapshot[0]}\n")
        f.close()

    if not has_passing and not has_failing:
        print("No tests exercise the faulty method")
    else:
        if not has_passing:
            print("No passing tests exercise the faulty method")
        if not has_failing:
            print("No failing tests exercise the faulty method")


if __name__ == "__main__":
    main()