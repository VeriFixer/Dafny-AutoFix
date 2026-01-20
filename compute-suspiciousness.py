def read_snapshots(enumeration):
    pass_snapshots = {}
    fail_snapshots = {}

    with open("snapshots-enum.csv" if enumeration else "snapshots-inv.csv", 'r') as file:
        passing = None
        for line in file:
            if line.strip() == "Running passing tests":
                passing = True
            elif line.strip() == "Running failing tests":
                passing = False
            elif passing != None:
                collection = pass_snapshots if passing else fail_snapshots
                snapshot = tuple(line.strip().split(','))
                if snapshot in collection:
                    collection[snapshot] += 1
                else:
                    collection[snapshot] = 1

    return (pass_snapshots, fail_snapshots)   


def compute_scores(pass_snapshots, fail_snapshots):
    scores = {}

    for snapshot in set(pass_snapshots) | set(fail_snapshots):
        alpha = 1 / 3
        beta = 2 / 3
        gamma = 1
        num_pass = pass_snapshots.get(snapshot, 0)
        num_fail = fail_snapshots.get(snapshot, 0)
        score = gamma + (alpha / (1 - alpha)) * (1 - beta + (beta * alpha ** num_pass) - (alpha ** num_fail))
        scores[snapshot] = round(score, 5)
    
    ordered_scores = {k: v for k, v in sorted(scores.items(), key=lambda item: (-item[1], int(item[0][0])))}
    return ordered_scores


def main():
    (pass_enum_snapshots, fail_enum_snapshots) = read_snapshots(True)
    (pass_inv_snapshots, fail_inv_snapshots) = read_snapshots(False)
    enum_snapshots = set(pass_enum_snapshots) | set(fail_enum_snapshots)
    inv_snapshots = set(pass_inv_snapshots) | set(fail_inv_snapshots)
    pass_snapshots = {
        key: pass_enum_snapshots.get(key, 0) + pass_inv_snapshots.get(key, 0)
        for key in set(pass_enum_snapshots) | set(pass_inv_snapshots)
    }
    fail_snapshots = {
        key: fail_enum_snapshots.get(key, 0) + fail_inv_snapshots.get(key, 0)
        for key in set(fail_enum_snapshots) | set(fail_inv_snapshots)
    }

    snapshot_scores = compute_scores(pass_snapshots, fail_snapshots)

    with open("snapshots-suspiciouness-score.csv", "w") as f:
        for snapshot, score in snapshot_scores.items():
            source = "both" if snapshot in enum_snapshots and snapshot in inv_snapshots \
                else "enum" if snapshot in enum_snapshots else "inv"
            f.write(f"{snapshot},{score},{source}\n")
        f.close()


if __name__ == "__main__":
    main()