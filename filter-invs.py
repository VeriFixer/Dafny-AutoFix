def read_invariants(passing):
    invs = {}

    with open("inv-pass.inv" if passing else "inv-fail.inv", 'r') as file:
        location = None
        new_location = False
        location_invs = []
        for line in file:
            if new_location:
                if (location != None):
                    invs[location] = location_invs
                location = line.strip()
                new_location = False
                location_invs = []
            elif line.strip() == "===========================================================================":
                new_location = True
            elif location != None:
                location_invs.append(line.strip())
        if len(location_invs) != 0:
            invs[location] = location_invs

    return invs


def compute_invariant_difference(passing_invs, failing_invs):
    filtered_invs = {}

    for location, invs in failing_invs.items():
        location_invs = []

        for inv in invs:
            if location not in passing_invs.keys() or inv not in passing_invs[location]:
                location_invs.append(inv)
        filtered_invs[location] = location_invs

    return filtered_invs


def main():
    passing_invs = read_invariants(True)
    failing_invs = read_invariants(False)
    filtered_invs = compute_invariant_difference(passing_invs, failing_invs)
    
    with open("inv.inv", "w") as f:
        for location, invs in filtered_invs.items():
            f.write(location + "\n")
            for inv in invs:
                f.write(inv + "\n")
        f.close()


if __name__ == "__main__":
    main()
