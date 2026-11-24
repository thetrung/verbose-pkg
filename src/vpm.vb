' verbose-pkg (vpm) — Serverless Package Manager for VerBose-Lang
' Inspired by Cargo, built natively in VerBose-Lang v0.6.0.
' Supports git repos, local paths, and portable source/binary .vpkg.tar.gz bundles.
Include "manifest.vb"

Public Static VPM_VERSION As String = "vpm 0.1.0 (VerBose-Lang Serverless Package Manager)"

Private Sub PrintHelp()
  Printf("%s\n\n", VPM_VERSION)
  Printf("Usage: vpm <command> [options]\n\n")
  Printf("Commands:\n")
  Printf("  init [name]              Scaffold a new VerBose project with verbose.toml\n")
  Printf("  add <name> <source>      Add a dependency (git URL, owner/repo, local path, or .vpkg.tar.gz) and install\n")
  Printf("  install                  Fetch dependencies, compile C shims into .a archives, and stage .vpkg/include\n")
  Printf("  build [-o out] [extra]   Build project entry point with all resolved deps & linker flags\n")
  Printf("  run [extra]              Build and run project entry point with all resolved deps\n")
  Printf("  test [target] [extra]    Run `verbose test` with all resolved deps\n")
  Printf("  pack [--binary]          Package current library into dist/<name>-<version>.vpkg.tar.gz\n")
  Printf("  list                     Display package metadata and resolved dependencies\n")
  Printf("  clean                    Remove .vpkg/ and build/ artifacts\n")
  Printf("  --version, -v            Show vpm version\n")
  Printf("  --help, -h               Show this help\n")
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

' --- vpm install ---
Private Function StageDependency(ByRef a As Arena, ByRef d As DepSpec, ByRef flagsOut As String) As Boolean
  Dim depDir As String = $".vpkg/deps/{d.name}"
  Printf("[vpm] Installing dependency '%s'...\n", d.name)

  VpmExec($"rm -rf '{depDir}' && mkdir -p '{depDir}'")

  Match d.kind With
    Case DEP_GIT Then
      Dim cmd As String = $"git clone --depth 1 '{d.source}' '{depDir}'"
      If VpmStrLen(d.branch) > 0 Then
        cmd = $"git clone --depth 1 --branch '{d.branch}' '{d.source}' '{depDir}'"
      End If
      If VpmExec(cmd) <> 0 Then
        Printf("[vpm] Error: git clone failed for '%s'\n", d.source)
        Return False
      End If
    Case DEP_PATH Then
      If VpmExec($"cp -R '{d.source}/.' '{depDir}/'") <> 0 Then
        Printf("[vpm] Error: failed to copy local dependency '%s'\n", d.source)
        Return False
      End If
    Case DEP_ARCHIVE Then
      If VpmStartsWith(d.source, "http://") Or VpmStartsWith(d.source, "https://") Then
        Dim tmpTar As String = $".vpkg/{d.name}.tar.gz"
        If VpmExec($"curl -fsSL '{d.source}' -o '{tmpTar}' && tar -xzf '{tmpTar}' -C '{depDir}' && rm -f '{tmpTar}'") <> 0 Then
          Printf("[vpm] Error: failed to download/extract archive '%s'\n", d.source)
          Return False
        End If
      Else
        If VpmExec($"tar -xzf '{d.source}' -C '{depDir}'") <> 0 Then
          Printf("[vpm] Error: failed to extract archive '%s'\n", d.source)
          Return False
        End If
      End If
    Case _ Then
      Printf("[vpm] Error: unknown dependency source kind for '%s'\n", d.name)
      Return False
  End Match

  ' Read the dependency's verbose.toml
  Dim dm As Manifest
  Dim depToml As String = $"{depDir}/verbose.toml"
  If Not ParseManifest(ByRef a, depToml, ByRef dm) Then
    Printf("[vpm] Warning: '%s' has no verbose.toml; using default src/ include dir\n", d.name)
    dm.name = d.name
    dm.includeDir = "src"
    dm.libEntry = ""
    dm.prebuilt = ""
    dm.cSourceCount = 0
    dm.cLibCount = 0
  End If

  ' Stage VerBose .vb files under .vpkg/include/<dep>/ AND mirror into .vpkg/include/
  Dim srcDir As String = $"{depDir}/{dm.includeDir}"
  VpmExec($"mkdir -p '.vpkg/include/{d.name}'")
  VpmExec($"if [ -d '{srcDir}' ]; then cp -R '{srcDir}/.' '.vpkg/include/{d.name}/'; fi")

  ' If the package defines an umbrella entry (e.g. raylib.vb), expose all its .vb files directly
  ' via `-I .vpkg/include/<dep>` so `Include "raylib.vb"` works out-of-the-box!
  flagsOut = $"{flagsOut} -I .vpkg/include/{d.name}"

  ' Check prebuilt archive first, else compile C sources into .vpkg/lib/lib<name>.a
  Dim archiveOut As String = $".vpkg/lib/lib{d.name}.a"
  Dim hasArchive As Boolean = False

  If VpmStrLen(dm.prebuilt) > 0 Then
    Dim prePath As String = $"{depDir}/{dm.prebuilt}"
    If VpmFileExists(prePath) Then
      VpmExec($"mkdir -p .vpkg/lib && cp '{prePath}' '{archiveOut}'")
      hasArchive = True
      Printf("[vpm]   using prebuilt binary library: %s\n", prePath)
    End If
  End If

  If Not hasArchive And dm.cSourceCount > 0 Then
    VpmExec($"mkdir -p .vpkg/obj/{d.name} .vpkg/lib")
    Dim objList As String = ""
    For i = 0 To dm.cSourceCount - 1 Do
      Dim cFile As String = $"{depDir}/{dm.cSources(i)}"
      Dim oFile As String = $".vpkg/obj/{d.name}/obj_{i}.o"
      Printf("[vpm]   compiling C shim: %s\n", dm.cSources(i))
      If VpmExec($"clang -O2 -c '{cFile}' -o '{oFile}'") <> 0 Then
        Printf("[vpm] Error: failed to compile C source '%s'\n", cFile)
        Return False
      End If
      objList = $"{objList} '{oFile}'"
    End For
    If VpmExec($"ar rcs '{archiveOut}' {objList}") <> 0 Then
      Printf("[vpm] Error: failed to archive '%s'\n", archiveOut)
      Return False
    End If
    hasArchive = True
  End If

  If hasArchive Then
    flagsOut = $"{flagsOut} {archiveOut}"
  End If

  For i = 0 To dm.cLibCount - 1 Do
    flagsOut = $"{flagsOut} -l{dm.cLibs(i)}"
  End For

  Return True
End Function

Private Function CmdInstall(ByRef a As Arena) As Integer
  Dim m As Manifest
  If Not ParseManifest(ByRef a, "verbose.toml", ByRef m) Then
    Printf("[vpm] Error: verbose.toml not found in current directory\n")
    Return 1
  End If

  VpmExec("mkdir -p .vpkg/deps .vpkg/include .vpkg/lib")
  Dim flags As String = "-I .vpkg/include"
  If VpmStrLen(m.includeDir) > 0 Then
    flags = $"{flags} -I {m.includeDir}"
  End If

  Dim lockTxt As String = "# Generated by vpm — do not edit manually\n"
  For i = 0 To m.depCount - 1 Do
    If Not StageDependency(ByRef a, ByRef m.deps(i), ByRef flags) Then
      Return 1
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
  Printf("[vpm] Installed %d dependency(ies). Flags -> .vpkg/flags.txt\n", m.depCount)
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
      ' Check if it looks like GitHub shorthand `owner/repo` (no leading . or / and does not exist locally)
      If Not VpmStartsWith(rawSrc, ".") And Not VpmStartsWith(rawSrc, "/") And VpmContainsChar(rawSrc, 47) And Not VpmFileExists($"{rawSrc}/verbose.toml") Then
        line = $"{depName} = {{ git = \"https://github.com/{rawSrc}\" }}\n"
      Else
        line = $"{depName} = {{ path = \"{rawSrc}\" }}\n"
      End If
    End If
  End If

  ' Ensure [dependencies] section exists
  Dim cur As String = VpmReadFile(ByRef a, "verbose.toml")
  If VpmIndexOf(cur, "[dependencies]") < 0 Then
    VpmAppendFile("verbose.toml", "\n[dependencies]\n")
  End If
  VpmAppendFile("verbose.toml", line)
  Printf("[vpm] Added %s to verbose.toml\n", depName)
  Return CmdInstall(ByRef a)
End Function

' --- vpm build / run / test ---
Private Function CollectExtraArgs(argc As Integer, argv() As Pointer, startIdx As Integer) As String
  Dim extra As String = ""
  For i = startIdx To argc - 1 Do
    Dim arg As String = argv(i)
    extra = $"{extra} '{arg}'"
  End For
  Return extra
End Function

Private Function EnsureInstalled(ByRef a As Arena) As String
  If Not VpmFileExists(".vpkg/flags.txt") Then
    If CmdInstall(ByRef a) <> 0 Then
      Return 0
    End If
  End If
  Return VpmReadFile(ByRef a, ".vpkg/flags.txt")
End Function

Private Function CmdBuildOrRun(ByRef a As Arena, isRun As Boolean, argc As Integer, argv() As Pointer) As Integer
  Dim m As Manifest
  If Not ParseManifest(ByRef a, "verbose.toml", ByRef m) Then
    Printf("[vpm] Error: verbose.toml not found\n")
    Return 1
  End If
  Dim flags As String = EnsureInstalled(ByRef a)
  If IsNull(flags) Then
    Return 1
  End If

  Dim compiler As String = ResolveCompiler()
  Dim extra As String = CollectExtraArgs(argc, argv, 2)
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

Private Function CmdTest(ByRef a As Arena, argc As Integer, argv() As Pointer) As Integer
  Dim m As Manifest
  If Not ParseManifest(ByRef a, "verbose.toml", ByRef m) Then
    Printf("[vpm] Error: verbose.toml not found\n")
    Return 1
  End If
  Dim flags As String = EnsureInstalled(ByRef a)
  If IsNull(flags) Then
    Return 1
  End If
  Dim compiler As String = ResolveCompiler()
  Dim extra As String = CollectExtraArgs(argc, argv, 2)
  Dim cmd As String = $"{compiler} test {extra} {flags}"
  Printf("[vpm] > %s\n", cmd)
  Dim rc As Integer = VpmExec(cmd)
  If rc <> 0 Then
    Return 1
  End If
  Return 0
End Function

' --- vpm pack [--binary] ---
Private Function CmdPack(ByRef a As Arena, argc As Integer, argv() As Pointer) As Integer
  Dim m As Manifest
  If Not ParseManifest(ByRef a, "verbose.toml", ByRef m) Then
    Printf("[vpm] Error: verbose.toml not found\n")
    Return 1
  End If

  Dim withBinary As Boolean = False
  For i = 2 To argc - 1 Do
    If VpmStrEq(argv(i), "--binary") Then
      withBinary = True
    End If
  End For

  Dim stageDir As String = $".vpkg/pack/{m.name}-{m.version}"
  VpmExec($"rm -rf '{stageDir}' && mkdir -p '{stageDir}' dist")
  VpmExec($"cp verbose.toml '{stageDir}/'")
  VpmExec($"if [ -f README.md ]; then cp README.md '{stageDir}/'; fi")
  VpmExec($"if [ -f LICENSE ]; then cp LICENSE '{stageDir}/'; fi")
  If VpmStrLen(m.includeDir) > 0 Then
    VpmExec($"if [ -d '{m.includeDir}' ]; then cp -R '{m.includeDir}' '{stageDir}/'; fi")
  End If

  ' Copy C sources too
  For i = 0 To m.cSourceCount - 1 Do
    Dim srcFile As String = m.cSources(i)
    VpmExec($"mkdir -p '{stageDir}/'$(dirname '{srcFile}') && cp '{srcFile}' '{stageDir}/{srcFile}'")
  End For

  Dim suffix As String = ""
  If withBinary And m.cSourceCount > 0 Then
    suffix = "-bin"
    VpmExec($"mkdir -p '{stageDir}/lib' '{stageDir}/obj'")
    Dim objList As String = ""
    For i = 0 To m.cSourceCount - 1 Do
      Dim cFile As String = m.cSources(i)
      Dim oFile As String = $"{stageDir}/obj/o_{i}.o"
      If VpmExec($"clang -O2 -c '{cFile}' -o '{oFile}'") <> 0 Then
        Printf("[vpm] Error: failed to compile '%s' for binary pack\n", cFile)
        Return 1
      End If
      objList = $"{objList} '{oFile}'"
    End For
    Dim libRel As String = $"lib/lib{m.name}.a"
    If VpmExec($"ar rcs '{stageDir}/{libRel}' {objList} && rm -rf '{stageDir}/obj'") <> 0 Then
      Printf("[vpm] Error: failed to create prebuilt static library\n")
      Return 1
    End If
    ' Append prebuilt entry into staged verbose.toml under [lib]
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
  Printf("C libs:  %d, C sources: %d\n", m.cLibCount, m.cSourceCount)
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

  Dim arena As Arena = ArenaCreate(2097152)
  Dim cmd As String = argv(1)
  Dim rc As Integer = 0

  Match cmd With
    Case "init" Then rc = CmdInit(ByRef arena, argc, argv)
    Case "add" Then rc = CmdAdd(ByRef arena, argc, argv)
    Case "install" Then rc = CmdInstall(ByRef arena)
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
