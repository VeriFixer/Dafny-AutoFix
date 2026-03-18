# A Replication of AutoFix for Fault Localization in Dafny

## Requirements

In order to run this project you need to have installed:

- Python3
- Java (version 22 or older)
- .NET 6.0
- [Daikon](https://plse.cs.washington.edu/daikon/download/doc/daikon.html#Installing-Daikon)

## Running the Project

1. **Build dafny**
```
cd dafny && make exe
```

2. **Install Z3**
```
cd dafny/Binaries
wget https://github.com/dafny-lang/solver-builds/releases/download/snapshot-2023-08-02/z3-4.12.1-x64-ubuntu-20.04-bin.zip
unzip z3-4.12.1-x64-ubuntu-20.04-bin.zip
mv z3-4.12.1 z3
chmod 755 z3
```

3. **Configure dotnet Z3 binding** (requires glibc version 2.35 or more recent)
```
wget -P ./autofix/lib https://github.com/Z3Prover/z3/releases/download/z3-4.14.1/z3-4.14.1-x64-glibc-2.35.zip
unzip -d ./autofix/lib/ ./autofix/lib/z3-4.14.1-x64-glibc-2.35.zip
```


4. **Build plugin**
```
cd autofix && dotnet build
```

5. **Run fault localization**
```
./run.sh program_file
```

6. **Analyze suspicious program states**

Open the `snapshots-suspiciousness-score.csv` file and analyze it. 

Each line corresponds to a snapshot *(l, p, v)*, where where *l* consists of a program location,
*p* of a boolean predicate, and *v* of a boolean value, and abstracting an execution of the program in which *p* evaluates to *v* at location *l*. The snapshots are ranked according to their suspiciousness score, which is also displayed in each line.
