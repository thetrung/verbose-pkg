' verbose-pkg (vpm) — Multithreaded Serverless Package Manager for VerBose-Lang
' Inspired by Cargo, built natively in 100% pure VerBose-Lang (Async/Await OS threads).
' Supports parallel dependency install/build, parallel workspace member build/test,
' parallel multi-suite testing, git repos, local paths, and .vpkg.tar.gz bundles.
Include "manifest.vb"

Public Static VPM_VERSION As String = "vpm 0.2.0 (VerBose-Lang Multithreaded Serverless Package Manager)"

' Thread-isolated per-slot worker state (each Async worker writes ONLY to its own index!)
Private Static g_jobs As Integer = 4
Private Static g_selfExe As String = "vpm"
Private Static g_deps(32) As DepSpec
Private Static g_depFlags(32) As Pointer
Private Static g_depMs(32) As Double
Private Static g_workMs(64) As Double

Private Sub PrintHelp()
  Printf("%s\n\n", VPM_VERSION)
  Printf("Usage: vpm <command> [options]\n\n")
  Printf("Commands:\n")
  Printf("  init [name]              Scaffold a new VerBose project with verbose.toml\n")
  Printf("  add <name> <source>      Add a dependency (git URL, owner/repo, local path, or .vpkg.tar.gz) and install\n")
  Printf("  install [-j N]           Fetch & build all dependencies in parallel, staging .vpkg/include & .vpkg/flags.txt\n")
  Printf("  build [-j N] [extra]     Build project (or all [workspace] member libs in parallel)\n")
  Printf("  run [extra]              Build and run project entry point with all resolved deps\n")
  Printf("  test [-j N] [--deps] [t] Run test suites (or [workspace] members / --deps libs) in parallel\n")
  Printf("  pack [--binary] [-j N]   Package current library into dist/<name>-<version>.vpkg.tar.gz\n")
  Printf("  list                     Display package/workspace metadata and dependencies\n")
  Printf("  clean                    Remove .vpkg/ and build/ artifacts\n")
  Printf("  --version, -v            Show vpm version\n")
  Printf("  --help, -h               Show this help\n\n")
  Printf("Concurrency:\n")
  Printf("  -j <N>, --jobs <N>       Max parallel worker threads (default: CPU count or $VPM_JOBS)\n")
End Sub

Private Function ResolveCompiler() As String
  Dim envBin As Pointer = getenv("VERBOSE_BIN")
  If Not IsNull(envBin) Then
    If VpmStrLen(envBin) > 0 Then
      Return envBin
    End If
  End If
  Return "verbose"
End Function

' Parse -j N / -jN / --jobs N from argv and collect remaining non-job args
Private Function CollectArgsFiltered(argc As Integer, argv() As Pointer, startIdx As Integer, ByRef outJobs As Integer, ByRef outDepsFlag As Boolean, ByRef outTarget As String) As String
  Dim extra As String = ""
  Dim i As Integer = startIdx
  While i < argc Do
    Dim arg As String = argv(i)
    If (VpmStrEq(arg, "-j") Or VpmStrEq(arg, "--jobs")) And ((i + 1) < argc) Then
      Dim jv As Integer = atoi(argv(i + 1))
      If jv >= 1 Then
        outJobs = jv
      End If
      i = i + 2
      Continue
    End If
    If VpmStartsWith(arg, "-j") And (VpmStrLen(arg) > 2) Then
      Dim jv As Integer = atoi(PtrAdd(arg, 2))
      If jv >= 1 Then
        outJobs = jv
      End If
      i = i + 1
      Continue
    End If
    If VpmStrEq(arg, "--deps") Or VpmStrEq(arg, "--all") Then
      outDepsFlag = True
      i = i + 1
      Continue
    End If
    If (VpmStrLen(outTarget) = 0) And (arg(0) <> 45) Then
      outTarget = arg
      i = i + 1
      Continue
    End If
    extra = $"{extra} '{arg}'"
    i = i + 1
  End While
  If outJobs < 1 Then
    outJobs = 1
  End If
  If outJobs > 32 Then
    outJobs = 32
  End If
  Return extra
End Function

' --- Async Worker: Run a single shell command & capture exit code + timing ---
Private Async Function ExecLoggedWorker(slot As Integer, cmd, logPath As String) As Integer
  Dim t0 As Double = VpmTimeMs()
  Dim fullCmd As String = $"({cmd}) > '{logPath}' 2>&1"
  Dim rc As Integer = VpmExec(fullCmd)
  g_workMs(slot) = VpmTimeMs() - t0
  If rc <> 0 Then
    Return 1
  End If
  Return 0
End Function

' --- Parallel C-Source Compilation Helper ---
Private Function CompileCSourcesParallel(baseDir, objDir, tagPrefix As String, ByRef cSources() As String, count, maxJobs As Integer, ByRef outObjList As String) As Boolean
  outObjList = ""
  If count <= 0 Then
    Return True
  End If
  VpmExec($"mkdir -p '{objDir}' .vpkg/logs")
  Dim tasks(16) As Task(Integer)
  Dim logs(16) As String
  Dim idx As Integer = 0
  While idx < count Do
    Dim batchEnd As Integer = idx + maxJobs
    If batchEnd > count Then
      batchEnd = count
    End If
    For j = idx To batchEnd - 1 Do
      Dim cFile As String = $"{baseDir}/{cSources(j)}"
      Dim oFile As String = $"{objDir}/obj_{j}.o"
      logs(j) = $".vpkg/logs/{tagPrefix}_cc_{j}.log"
      Dim ccCmd As String = $"clang -O2 -c '{cFile}' -o '{oFile}'"
      tasks(j) = ExecLoggedWorker(j, ccCmd, logs(j))
      outObjList = $"{outObjList} '{oFile}'"
    End For
    Dim failed As Boolean = False
    For j = idx To batchEnd - 1 Do
      Dim rc As Integer = Await tasks(j)
      If rc <> 0 Then
        Dim tmpA As Arena = ArenaCreate(65536)
        Printf("[vpm] Error: failed to compile '%s/%s':\n", baseDir, cSources(j))
        VpmPrintFile(ByRef tmpA, logs(j))
        ArenaDestroy(ByRef tmpA)
        failed = True
      End If
    End For
    If failed Then
      Return False
    End If
    idx = batchEnd
  End While
  Return True
End Function

' --- vpm init [name] ---
Private Function CmdInit(ByRef a As Arena, argc As Integer, argv() As Pointer) As Integer
  Dim projName As String = "hello-verbose"
  Dim baseDir As String = "."
  If argc >= 3 Then
    projName = argv(2)
    baseDir = projName
    VpmExec($"mkdir -p '{baseDir}/src'")
  Else
    VpmExec("mkdir -p src")
  End If

  Dim tomlPath As String = $"{baseDir}/verbose.toml"
  If VpmFileExists(tomlPath) Then
    Printf("[vpm] verbose.toml already exists at %s\n", tomlPath)
    Return 1
  End If

  Dim toml As String = "[package]\n" &
    $"name = \"{projName}\"\n" &
    "version = \"0.1.0\"\n" &
    $"description = \"VerBose-Lang project {projName}\"\n" &
    "entry = \"src/main.vb\"\n\n" &
    "[lib]\n" &
    "include_dir = \"src\"\n" &
    "c_sources = []\n" &
    "c_libs = []\n\n" &
    "[dependencies]\n"
  VpmWriteFile(tomlPath, toml)

  Dim mainPath As String = $"{baseDir}/src/main.vb"
  If Not VpmFileExists(mainPath) Then
    Dim mainSrc As String = $"' {projName} — VerBose-Lang Entry Point\n\n" &
      "Public Function main() As Integer\n" &
      $"  Printf(\"Hello from {projName}!\\n\")\n" &
      "  Return 0\n" &
      "End Function\n"
    VpmWriteFile(mainPath, mainSrc)
  End If

  Dim giPath As String = $"{baseDir}/.gitignore"
  If Not VpmFileExists(giPath) Then
    VpmWriteFile(giPath, ".vpkg/\nbuild/\ndist/\n*.ll\n*.o\n")
  End If

  Printf("[vpm] Initialized project '%s' at %s\n", projName, baseDir)
  Return 0
End Function

' --- Async Worker: Fetch, Stage & Build One Dependency Independently ---
Private Async Function StageDepWorker(idx As Integer) As Integer
  Dim t0 As Double = VpmTimeMs()
  Dim localA As Arena = ArenaCreate(262144)
  Dim d As DepSpec = g_deps(idx)
  Dim depDir As String = $".vpkg/deps/{d.name}"
  Dim logPath As String = $".vpkg/logs/dep_{idx}_{d.name}.log"
  Dim depFlags As String = ""

  VpmExec($"rm -rf '{depDir}' && mkdir -p '{depDir}'")

  Dim fetchOk As Boolean = True
  Match d.kind With
    Case DEP_GIT Then
      Dim cmd As String = $"git clone --depth 1 '{d.source}' '{depDir}' > '{logPath}' 2>&1"
      If VpmStrLen(d.branch) > 0 Then
        cmd = $"git clone --depth 1 --branch '{d.branch}' '{d.source}' '{depDir}' > '{logPath}' 2>&1"
      End If
      If VpmExec(cmd) <> 0 Then
        fetchOk = False
      End If
    Case DEP_PATH Then
      If VpmExec($"cp -R '{d.source}/.' '{depDir}/' > '{logPath}' 2>&1") <> 0 Then
        fetchOk = False
      End If
    Case DEP_ARCHIVE Then
      If VpmStartsWith(d.source, "http://") Or VpmStartsWith(d.source, "https://") Then
        Dim tmpTar As String = $".vpkg/{d.name}_{idx}.tar.gz"
        If VpmExec($"curl -fsSL '{d.source}' -o '{tmpTar}' && tar -xzf '{tmpTar}' -C '{depDir}' && rm -f '{tmpTar}' > '{logPath}' 2>&1") <> 0 Then
          fetchOk = False
        End If
      Else
        If VpmExec($"tar -xzf '{d.source}' -C '{depDir}' > '{logPath}' 2>&1") <> 0 Then
          fetchOk = False
        End If
      End If
    Case _ Then
      fetchOk = False
  End Match

  If Not fetchOk Then
    g_depMs(idx) = VpmTimeMs() - t0
    ArenaDestroy(ByRef localA)
    Return 1
  End If

  Dim dm As Manifest
  Dim depToml As String = $"{depDir}/verbose.toml"
  If Not ParseManifest(ByRef localA, depToml, ByRef dm) Then
    dm.name = d.name
    dm.includeDir = "src"
    dm.libEntry = ""
    dm.prebuilt = ""
    dm.buildCmd = ""
    dm.cSourceCount = 0
    dm.cLibCount = 0
  End If

  ' Stage VerBose .vb files under .vpkg/include/<dep>/
  Dim srcDir As String = $"{depDir}/{dm.includeDir}"
  VpmExec($"mkdir -p '.vpkg/include/{d.name}'")
  VpmExec($"if [ -d '{srcDir}' ]; then cp -R '{srcDir}/.' '.vpkg/include/{d.name}/'; fi")
  depFlags = $"{depFlags} -I .vpkg/include/{d.name}"

  ' Optional custom build_cmd for third-party native engines
  If VpmStrLen(dm.buildCmd) > 0 Then
    If VpmExec($"(cd '{depDir}' && {dm.buildCmd}) >> '{logPath}' 2>&1") <> 0 Then
      g_depMs(idx) = VpmTimeMs() - t0
      ArenaDestroy(ByRef localA)
      Return 1
    End If
  End If

  ' Prebuilt archive or compile C sources into .vpkg/lib/lib<name>.a
  Dim archiveOut As String = $".vpkg/lib/lib{d.name}.a"
  Dim hasArchive As Boolean = False

  If VpmStrLen(dm.prebuilt) > 0 Then
    Dim prePath As String = $"{depDir}/{dm.prebuilt}"
    If VpmFileExists(prePath) Then
      VpmExec($"cp '{prePath}' '{archiveOut}'")
      hasArchive = True
    End If
  End If

  If (Not hasArchive) And (dm.cSourceCount > 0) Then
    Dim objDir As String = $".vpkg/obj/{d.name}"
    VpmExec($"mkdir -p '{objDir}'")
    Dim objList As String = ""
    For ci = 0 To dm.cSourceCount - 1 Do
      Dim cFile As String = $"{depDir}/{dm.cSources(ci)}"
      Dim oFile As String = $"{objDir}/obj_{ci}.o"
      If VpmExec($"clang -O2 -c '{cFile}' -o '{oFile}' >> '{logPath}' 2>&1") <> 0 Then
        g_depMs(idx) = VpmTimeMs() - t0
        ArenaDestroy(ByRef localA)
        Return 1
      End If
      objList = $"{objList} '{oFile}'"
    End For
    If VpmExec($"ar rcs '{archiveOut}' {objList} >> '{logPath}' 2>&1") <> 0 Then
      g_depMs(idx) = VpmTimeMs() - t0
      ArenaDestroy(ByRef localA)
      Return 1
    End If
    hasArchive = True
  End If

  If hasArchive Then
    depFlags = $"{depFlags} {archiveOut}"
  End If

  For li = 0 To dm.cLibCount - 1 Do
    depFlags = $"{depFlags} -l{dm.cLibs(li)}"
  End For

  ' Copy flags string to thread-safe heap buffer before destroying local Arena
  Dim flen As Integer = VpmStrLen(depFlags)
  Dim heapFlags As Pointer = HeapAllocBytes(flen + 1)
  If flen > 0 Then
    memcpy(heapFlags, depFlags, Long(flen))
  End If
  heapFlags(flen) = Byte(0)
  g_depFlags(idx) = heapFlags
  g_depMs(idx) = VpmTimeMs() - t0
  ArenaDestroy(ByRef localA)
  Return 0
End Function

' --- Parallel Workspace Helper (install / build / test across [workspace].members) ---
Private Function RunWorkspaceParallel(ByRef a As Arena, ByRef m As Manifest, subCmd, extra As String, jobs As Integer) As Integer
  Dim tStart As Double = VpmTimeMs()
  VpmExec("mkdir -p .vpkg/logs")
  Dim total As Integer = m.memberCount
  Dim workers As Integer = jobs
  If workers > total Then
    workers = total
  End If
  Printf("[vpm] Workspace '%s': running `%s` across %d member(s) using %d thread(s)...\n",
    m.name, subCmd, total, workers)

  Dim subJobs As Integer = jobs / total
  If subJobs < 1 Then
    subJobs = 1
  End If
  Dim compiler As String = ResolveCompiler()

  Dim tasks(32) As Task(Integer)
  Dim logs(32) As String
  Dim passed As Integer = 0
  Dim failed As Integer = 0

  Dim idx As Integer = 0
  While idx < total Do
    Dim batchEnd As Integer = idx + workers
    If batchEnd > total Then
      batchEnd = total
    End If
    For j = idx To batchEnd - 1 Do
      Dim dir As String = m.members(j)
      logs(j) = $".vpkg/logs/ws_{subCmd}_{j}.log"
      Dim cmd As String = $"cd '{dir}' && VERBOSE_BIN='{compiler}' '{g_selfExe}' {subCmd} -j {subJobs} {extra}"
      tasks(j) = ExecLoggedWorker(j, cmd, logs(j))
    End For
    For j = idx To batchEnd - 1 Do
      Dim rc As Integer = Await tasks(j)
      If rc = 0 Then
        passed = passed + 1
        Printf("[vpm]   [OK]   %s (%.0f ms)\n", m.members(j), g_workMs(j))
      Else
        failed = failed + 1
        Printf("[vpm]   [FAIL] %s (%.0f ms)\n", m.members(j), g_workMs(j))
        VpmPrintFile(ByRef a, logs(j))
      End If
    End For
    idx = batchEnd
  End While

  Dim totalMs As Double = VpmTimeMs() - tStart
  Printf("[vpm] Workspace %s: %d/%d passed, %d failed (%.0f ms, %d thread(s))\n",
    subCmd, passed, total, failed, totalMs, workers)
  If failed > 0 Then
    Return 1
  End If
  Return 0
End Function

' --- vpm install [-j N] ---
Private Function CmdInstall(ByRef a As Arena, jobs As Integer) As Integer
  Dim m As Manifest
  If Not ParseManifest(ByRef a, "verbose.toml", ByRef m) Then
    Printf("[vpm] Error: verbose.toml not found in current directory\n")
    Return 1
  End If

  If (m.memberCount > 0) And (m.depCount = 0) Then
    Return RunWorkspaceParallel(ByRef a, ByRef m, "install", "", jobs)
  End If

  Dim tStart As Double = VpmTimeMs()
  VpmExec("mkdir -p .vpkg/deps .vpkg/include .vpkg/lib .vpkg/logs")
  Dim flags As String = "-I .vpkg/include"
  If VpmStrLen(m.includeDir) > 0 Then
    flags = $"{flags} -I {m.includeDir}"
  End If

  Dim total As Integer = m.depCount
  If total > 0 Then
    Dim workers As Integer = jobs
    If workers > total Then
      workers = total
    End If
    Printf("[vpm] Installing %d dependency(ies) using %d thread(s)...\n", total, workers)

    For i = 0 To total - 1 Do
      g_deps(i) = m.deps(i)
      g_depFlags(i) = Pointer(0)
      g_depMs(i) = 0.0
    End For

    Dim tasks(32) As Task(Integer)
    Dim failed As Integer = 0
    Dim idx As Integer = 0
    While idx < total Do
      Dim batchEnd As Integer = idx + workers
      If batchEnd > total Then
        batchEnd = total
      End If
      For j = idx To batchEnd - 1 Do
        tasks(j) = StageDepWorker(j)
      End For
      For j = idx To batchEnd - 1 Do
        Dim rc As Integer = Await tasks(j)
        If rc = 0 Then
          Printf("[vpm]   [OK]   %s (%.0f ms)\n", m.deps(j).name, g_depMs(j))
        Else
          failed = failed + 1
          Printf("[vpm]   [FAIL] %s (%.0f ms)\n", m.deps(j).name, g_depMs(j))
          VpmPrintFile(ByRef a, $".vpkg/logs/dep_{j}_{m.deps(j).name}.log")
        End If
      End For
      idx = batchEnd
    End While

    If failed > 0 Then
      For i = 0 To total - 1 Do
        If Not IsNull(g_depFlags(i)) Then
          HeapFree(g_depFlags(i))
          g_depFlags(i) = Pointer(0)
        End If
      End For
      Return 1
    End If
  End If

  ' Assemble flags.txt & verbose.lock in deterministic manifest order
  Dim lockTxt As String = "# Generated by vpm — do not edit manually\n"
  For i = 0 To total - 1 Do
    If Not IsNull(g_depFlags(i)) Then
      Dim df As String = g_depFlags(i)
      flags = $"{flags}{df}"
      HeapFree(g_depFlags(i))
      g_depFlags(i) = Pointer(0)
    End If
    lockTxt = $"{lockTxt}{m.deps(i).name} = \"{m.deps(i).source}\"\n"
  End For

  ' Also include root project's own c_sources & c_libs
  For i = 0 To m.cSourceCount - 1 Do
    flags = $"{flags} {m.cSources(i)}"
  End For
  For i = 0 To m.cLibCount - 1 Do
    flags = $"{flags} -l{m.cLibs(i)}"
  End For

  VpmWriteFile(".vpkg/flags.txt", flags)
  VpmWriteFile("verbose.lock", lockTxt)
  Printf("[vpm] Installed %d dependency(ies) in %.0f ms. Flags -> .vpkg/flags.txt\n", total, VpmTimeMs() - tStart)
  Return 0
End Function

' --- vpm add <name> <source> ---
Private Function CmdAdd(ByRef a As Arena, argc As Integer, argv() As Pointer) As Integer
  If argc < 4 Then
    Printf("Usage: vpm add <name> <source>\n")
    Return 1
  End If
  If Not VpmFileExists("verbose.toml") Then
    Printf("[vpm] Error: verbose.toml not found. Run `vpm init` first.\n")
    Return 1
  End If

  Dim depName As String = argv(2)
  Dim rawSrc As String = argv(3)
  Dim line As String = ""

  If VpmStartsWith(rawSrc, "http://") Or VpmStartsWith(rawSrc, "https://") Or VpmStartsWith(rawSrc, "git@") Then
    If VpmEndsWith(rawSrc, ".tar.gz") Or VpmEndsWith(rawSrc, ".tgz") Then
      line = $"{depName} = {{ archive = \"{rawSrc}\" }}\n"
    Else
      line = $"{depName} = {{ git = \"{rawSrc}\" }}\n"
    End If
  Else
    If VpmEndsWith(rawSrc, ".tar.gz") Or VpmEndsWith(rawSrc, ".tgz") Then
      line = $"{depName} = {{ archive = \"{rawSrc}\" }}\n"
    Else
      If Not VpmStartsWith(rawSrc, ".") And Not VpmStartsWith(rawSrc, "/") And VpmContainsChar(rawSrc, 47) And Not VpmFileExists($"{rawSrc}/verbose.toml") Then
        line = $"{depName} = {{ git = \"https://github.com/{rawSrc}\" }}\n"
      Else
        line = $"{depName} = {{ path = \"{rawSrc}\" }}\n"
      End If
    End If
  End If

  Dim cur As String = VpmReadFile(ByRef a, "verbose.toml")
  If VpmIndexOf(cur, "[dependencies]") < 0 Then
    VpmAppendFile("verbose.toml", "\n[dependencies]\n")
  End If
  VpmAppendFile("verbose.toml", line)
  Printf("[vpm] Added %s to verbose.toml\n", depName)
  Return CmdInstall(ByRef a, g_jobs)
End Function

Private Function EnsureInstalled(ByRef a As Arena, jobs As Integer) As String
  If Not VpmFileExists(".vpkg/flags.txt") Then
    If CmdInstall(ByRef a, jobs) <> 0 Then
      Return 0
    End If
  End If
  Return VpmReadFile(ByRef a, ".vpkg/flags.txt")
End Function

' --- vpm build / run ---
Private Function CmdBuildOrRun(ByRef a As Arena, isRun As Boolean, argc As Integer, argv() As Pointer) As Integer
  Dim m As Manifest
  If Not ParseManifest(ByRef a, "verbose.toml", ByRef m) Then
    Printf("[vpm] Error: verbose.toml not found\n")
    Return 1
  End If

  Dim jobs As Integer = g_jobs
  Dim depsFlag As Boolean = False
  Dim target As String = ""
  Dim extra As String = CollectArgsFiltered(argc, argv, 2, ByRef jobs, ByRef depsFlag, ByRef target)
  If VpmStrLen(target) > 0 Then
    extra = $"'{target}' {extra}"
  End If

  If (Not isRun) And (m.memberCount > 0) Then
    Return RunWorkspaceParallel(ByRef a, ByRef m, "build", extra, jobs)
  End If

  Dim flags As String = EnsureInstalled(ByRef a, jobs)
  If IsNull(flags) Then
    Return 1
  End If

  Dim compiler As String = ResolveCompiler()

  ' Library-only package without main entry (e.g. workspace member lib): type-check & emit IR to validate!
  If (Not isRun) And (Not VpmFileExists(m.entry)) And (VpmStrLen(m.libEntry) > 0) Then
    Dim libSrc As String = $"{m.includeDir}/{m.libEntry}"
    If VpmFileExists(libSrc) Then
      Dim objList As String = ""
      If m.cSourceCount > 0 Then
        If Not CompileCSourcesParallel(".", ".vpkg/obj/self", "self", ByRef m.cSources, m.cSourceCount, jobs, ByRef objList) Then
          Return 1
        End If
      End If
      VpmExec("mkdir -p build")
      Dim checkCmd As String = $"{compiler} '{libSrc}' {flags} --emit-only -o 'build/{m.name}.ll' {extra}"
      Printf("[vpm] > %s\n", checkCmd)
      If VpmExec(checkCmd) <> 0 Then
        Return 1
      End If
      Return 0
    End If
  End If

  Dim cmd As String = ""
  If isRun Then
    cmd = $"{compiler} '{m.entry}' {flags} {extra}"
  Else
    VpmExec("mkdir -p build")
    Dim outBin As String = $"build/{m.name}"
    cmd = $"{compiler} '{m.entry}' {flags} --no-run -o '{outBin}' {extra}"
  End If

  Printf("[vpm] > %s\n", cmd)
  Dim rc As Integer = VpmExec(cmd)
  If rc <> 0 Then
    Return 1
  End If
  Return 0
End Function

' --- vpm test [-j N] [--deps] [target] ---
Private Function CmdTest(ByRef a As Arena, argc As Integer, argv() As Pointer) As Integer
  Dim m As Manifest
  If Not ParseManifest(ByRef a, "verbose.toml", ByRef m) Then
    Printf("[vpm] Error: verbose.toml not found\n")
    Return 1
  End If

  Dim jobs As Integer = g_jobs
  Dim testDeps As Boolean = False
  Dim target As String = ""
  Dim extra As String = CollectArgsFiltered(argc, argv, 2, ByRef jobs, ByRef testDeps, ByRef target)

  ' Workspace mode: test all member libraries in parallel!
  If m.memberCount > 0 Then
    Dim wsExtra As String = extra
    If VpmStrLen(target) > 0 Then
      wsExtra = $"'{target}' {wsExtra}"
    End If
    Return RunWorkspaceParallel(ByRef a, ByRef m, "test", wsExtra, jobs)
  End If

  Dim flags As String = EnsureInstalled(ByRef a, jobs)
  If IsNull(flags) Then
    Return 1
  End If
  Dim compiler As String = ResolveCompiler()

  ' Collect all test jobs (root test files + optional --deps library tests)
  Dim jobLabels(64) As String
  Dim jobCmds(64) As String
  Dim jobCount As Integer = 0

  Dim scanDir As String = "tests"
  If VpmStrLen(target) > 0 Then
    If VpmDirExists(target) Then
      scanDir = target
    Else
      ' Single explicit test file
      If Not testDeps Then
        Dim singleCmd As String = $"{compiler} test '{target}' {extra} {flags}"
        Printf("[vpm] > %s\n", singleCmd)
        If VpmExec(singleCmd) <> 0 Then
          Return 1
        End If
        Return 0
      End If
      jobLabels(0) = target
      jobCmds(0) = $"{compiler} test '{target}' {extra} {flags}"
      jobCount = 1
      scanDir = ""
    End If
  End If

  If VpmStrLen(scanDir) > 0 Then
    Dim rootTests(64) As String
    Dim nRoot As Integer = VpmListTestFiles(ByRef a, scanDir, ByRef rootTests, 64)
    For i = 0 To nRoot - 1 Do
      If jobCount < 64 Then
        jobLabels(jobCount) = rootTests(i)
        jobCmds(jobCount) = $"{compiler} test '{rootTests(i)}' {extra} {flags}"
        jobCount = jobCount + 1
      End If
    End For
  End If

  ' Optionally test every installed dependency library in parallel (--deps)
  If testDeps Then
    For di = 0 To m.depCount - 1 Do
      Dim depDir As String = $".vpkg/deps/{m.deps(di).name}"
      Dim depTestsDir As String = $"{depDir}/tests"
      If VpmDirExists(depTestsDir) And (jobCount < 64) Then
        jobLabels(jobCount) = $"dep:{m.deps(di).name}"
        If VpmFileExists($"{depDir}/verbose.toml") Then
          jobCmds(jobCount) = $"cd '{depDir}' && VERBOSE_BIN='{compiler}' '{g_selfExe}' test -j 1"
        Else
          jobCmds(jobCount) = $"cd '{depDir}' && '{compiler}' test tests -I src"
        End If
        jobCount = jobCount + 1
      End If
    End For
  End If

  If jobCount = 0 Then
    Dim fallbackCmd As String = $"{compiler} test {extra} {flags}"
    Printf("[vpm] > %s\n", fallbackCmd)
    If VpmExec(fallbackCmd) <> 0 Then
      Return 1
    End If
    Return 0
  End If

  If (jobCount = 1) And (Not testDeps) Then
    Printf("[vpm] > %s\n", jobCmds(0))
    If VpmExec(jobCmds(0)) <> 0 Then
      Return 1
    End If
    Return 0
  End If

  ' Run all discovered test suites in parallel across up to `jobs` threads!
  Dim tStart As Double = VpmTimeMs()
  VpmExec("mkdir -p .vpkg/logs")
  Dim workers As Integer = jobs
  If workers > jobCount Then
    workers = jobCount
  End If
  Printf("[vpm] Running %d test suite(s) in parallel using %d thread(s)...\n", jobCount, workers)

  Dim tasks(64) As Task(Integer)
  Dim logs(64) As String
  Dim passed As Integer = 0
  Dim failed As Integer = 0

  Dim idx As Integer = 0
  While idx < jobCount Do
    Dim batchEnd As Integer = idx + workers
    If batchEnd > jobCount Then
      batchEnd = jobCount
    End If
    For j = idx To batchEnd - 1 Do
      logs(j) = $".vpkg/logs/test_{j}.log"
      tasks(j) = ExecLoggedWorker(j, jobCmds(j), logs(j))
    End For
    For j = idx To batchEnd - 1 Do
      Dim rc As Integer = Await tasks(j)
      If rc = 0 Then
        passed = passed + 1
        Printf("[vpm]   [PASS] %s (%.0f ms)\n", jobLabels(j), g_workMs(j))
      Else
        failed = failed + 1
        Printf("[vpm]   [FAIL] %s (%.0f ms)\n", jobLabels(j), g_workMs(j))
        VpmPrintFile(ByRef a, logs(j))
      End If
    End For
    idx = batchEnd
  End While

  Printf("[vpm] Test suites: %d/%d passed, %d failed (%.0f ms, %d thread(s))\n",
    passed, jobCount, failed, VpmTimeMs() - tStart, workers)
  If failed > 0 Then
    Return 1
  End If
  Return 0
End Function

' --- vpm pack [--binary] [-j N] ---
Private Function CmdPack(ByRef a As Arena, argc As Integer, argv() As Pointer) As Integer
  Dim m As Manifest
  If Not ParseManifest(ByRef a, "verbose.toml", ByRef m) Then
    Printf("[vpm] Error: verbose.toml not found\n")
    Return 1
  End If

  Dim withBinary As Boolean = False
  Dim jobs As Integer = g_jobs
  Dim i As Integer = 2
  While i < argc Do
    If VpmStrEq(argv(i), "--binary") Then
      withBinary = True
    Else
      If (VpmStrEq(argv(i), "-j") Or VpmStrEq(argv(i), "--jobs")) And ((i + 1) < argc) Then
        Dim jv As Integer = atoi(argv(i + 1))
        If jv >= 1 Then
          jobs = jv
        End If
        i = i + 2
        Continue
      End If
    End If
    i = i + 1
  End While

  Dim stageDir As String = $".vpkg/pack/{m.name}-{m.version}"
  VpmExec($"rm -rf '{stageDir}' && mkdir -p '{stageDir}' dist")
  VpmExec($"cp verbose.toml '{stageDir}/'")
  VpmExec($"if [ -f README.md ]; then cp README.md '{stageDir}/'; fi")
  VpmExec($"if [ -f LICENSE ]; then cp LICENSE '{stageDir}/'; fi")
  If VpmStrLen(m.includeDir) > 0 Then
    VpmExec($"if [ -d '{m.includeDir}' ]; then cp -R '{m.includeDir}' '{stageDir}/'; fi")
  End If

  For ci = 0 To m.cSourceCount - 1 Do
    Dim srcFile As String = m.cSources(ci)
    VpmExec($"mkdir -p '{stageDir}/'$(dirname '{srcFile}') && cp '{srcFile}' '{stageDir}/{srcFile}'")
  End For

  Dim suffix As String = ""
  If withBinary And m.cSourceCount > 0 Then
    suffix = "-bin"
    VpmExec($"mkdir -p '{stageDir}/lib' '{stageDir}/obj'")
    Dim objList As String = ""
    If Not CompileCSourcesParallel(".", $"{stageDir}/obj", "pack", ByRef m.cSources, m.cSourceCount, jobs, ByRef objList) Then
      Return 1
    End If
    Dim libRel As String = $"lib/lib{m.name}.a"
    If VpmExec($"ar rcs '{stageDir}/{libRel}' {objList} && rm -rf '{stageDir}/obj'") <> 0 Then
      Printf("[vpm] Error: failed to create prebuilt static library\n")
      Return 1
    End If
    Dim stagedToml As String = VpmReadFile(ByRef a, $"{stageDir}/verbose.toml")
    Dim libPos As Integer = VpmIndexOf(stagedToml, "[lib]")
    If libPos >= 0 Then
      Dim before As String = VpmDupRange(ByRef a, stagedToml, 0, libPos + 5)
      Dim after As String = VpmDupRange(ByRef a, stagedToml, libPos + 5, VpmStrLen(stagedToml) - (libPos + 5))
      Dim patched As String = $"{before}\nprebuilt = \"{libRel}\"{after}"
      VpmWriteFile($"{stageDir}/verbose.toml", patched)
    End If
  End If

  Dim outArchive As String = $"dist/{m.name}-{m.version}{suffix}.vpkg.tar.gz"
  If VpmExec($"tar -czf '{outArchive}' -C '{stageDir}' .") <> 0 Then
    Printf("[vpm] Error: tar failed\n")
    Return 1
  End If
  Printf("[vpm] Packed -> %s\n", outArchive)
  Return 0
End Function

' --- vpm list ---
Private Function CmdList(ByRef a As Arena) As Integer
  Dim m As Manifest
  If Not ParseManifest(ByRef a, "verbose.toml", ByRef m) Then
    Printf("[vpm] Error: verbose.toml not found\n")
    Return 1
  End If
  Printf("Package: %s v%s\n", m.name, m.version)
  Printf("Entry:   %s (include_dir: %s)\n", m.entry, m.includeDir)
  Printf("C libs:  %d, C sources: %d, Default threads: %d\n", m.cLibCount, m.cSourceCount, g_jobs)
  If m.memberCount > 0 Then
    Printf("Workspace Members (%d):\n", m.memberCount)
    For i = 0 To m.memberCount - 1 Do
      Printf("  - %s\n", m.members(i))
    End For
  End If
  Printf("Dependencies (%d):\n", m.depCount)
  For i = 0 To m.depCount - 1 Do
    Dim kstr As String = "unknown"
    Match m.deps(i).kind With
      Case DEP_GIT Then kstr = "git"
      Case DEP_PATH Then kstr = "path"
      Case DEP_ARCHIVE Then kstr = "archive"
      Case _ Then
    End Match
    Printf("  - %s [%s] %s\n", m.deps(i).name, kstr, m.deps(i).source)
  End For
  Return 0
End Function

Public Function main(argc As Integer, argv() As Pointer) As Integer
  If argc < 2 Then
    PrintHelp()
    Return 0
  End If

  Dim arena As Arena = ArenaCreate(4194304)
  g_jobs = VpmCpuCount()
  g_selfExe = VpmSelfExe(ByRef arena, argv(0))
  Dim cmd As String = argv(1)
  Dim rc As Integer = 0

  Match cmd With
    Case "init" Then rc = CmdInit(ByRef arena, argc, argv)
    Case "add" Then rc = CmdAdd(ByRef arena, argc, argv)
    Case "install" Then
      Dim jobs As Integer = g_jobs
      Dim dummyDeps As Boolean = False
      Dim dummyTgt As String = ""
      CollectArgsFiltered(argc, argv, 2, ByRef jobs, ByRef dummyDeps, ByRef dummyTgt)
      rc = CmdInstall(ByRef arena, jobs)
    Case "build" Then rc = CmdBuildOrRun(ByRef arena, False, argc, argv)
    Case "run" Then rc = CmdBuildOrRun(ByRef arena, True, argc, argv)
    Case "test" Then rc = CmdTest(ByRef arena, argc, argv)
    Case "pack" Then rc = CmdPack(ByRef arena, argc, argv)
    Case "list" Then rc = CmdList(ByRef arena)
    Case "clean" Then
      VpmExec("rm -rf .vpkg build")
      Printf("[vpm] Cleaned .vpkg/ and build/\n")
    Case "--version", "-v", "version" Then
      Printf("%s\n", VPM_VERSION)
    Case "--help", "-h", "help" Then
      PrintHelp()
    Case _ Then
      Printf("[vpm] Unknown command: %s\n", cmd)
      PrintHelp()
      rc = 1
  End Match

  ArenaDestroy(ByRef arena)
  Return rc
End Function
