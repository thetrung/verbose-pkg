' verbose-pkg (vpm) — Automated Manifest, Pack & End-to-End Dependency Tests
Include "manifest.vb"

Test "Parse verbose.toml manifest with [package], [lib] and [dependencies]"
  Dim a As Arena = ArenaCreate(131072)
  Dim tmpPath As String = "/tmp/vpm_test_manifest.toml"
  Dim sample As String = "[package]\n" &
    "name = \"demo-game\"\n" &
    "version = \"0.2.0\"\n" &
    "entry = \"src/game.vb\"\n\n" &
    "[lib]\n" &
    "include_dir = \"src\"\n" &
    "entry = \"game.vb\"\n" &
    "c_sources = [\"csrc/shim.c\", \"csrc/extra.c\"]\n" &
    "c_libs = [\"raylib\", \"m\"]\n\n" &
    "[dependencies]\n" &
    "verbose-raylib = { git = \"https://github.com/thetrung/verbose-raylib\", branch = \"main\" }\n" &
    "local-util = { path = \"../util\" }\n" &
    "prebuilt-pkg = { archive = \"dist/pkg-0.1.0-bin.vpkg.tar.gz\" }\n"

  Assert VpmWriteFile(tmpPath, sample), "write sample toml"
  Dim m As Manifest
  Assert ParseManifest(ByRef a, tmpPath, ByRef m), "ParseManifest failed"
  Assert VpmStrEq(m.name, "demo-game"), "package name"
  Assert VpmStrEq(m.version, "0.2.0"), "package version"
  Assert VpmStrEq(m.entry, "src/game.vb"), "package entry"
  Assert m.cSourceCount = 2, "c_sources count"
  Assert VpmStrEq(m.cSources(0), "csrc/shim.c"), "c_sources(0)"
  Assert m.cLibCount = 2, "c_libs count"
  Assert VpmStrEq(m.cLibs(0), "raylib"), "c_libs(0)"
  Assert m.depCount = 3, "dep count"
  Assert m.deps(0).kind = DEP_GIT And VpmStrEq(m.deps(0).branch, "main"), "git dep"
  Assert m.deps(1).kind = DEP_PATH And VpmStrEq(m.deps(1).source, "../util"), "path dep"
  Assert m.deps(2).kind = DEP_ARCHIVE, "archive dep"

  VpmExec($"rm -f '{tmpPath}'")
  ArenaDestroy(ByRef a)
End Test

Test "End-to-end: pack a C+VerBose library (--binary), install via archive into fresh app, build & run"
  Dim a As Arena = ArenaCreate(131072)
  Dim root As String = "/tmp/vpm_e2e_test"
  VpmExec($"rm -rf '{root}' && mkdir -p '{root}/mylib/src' '{root}/mylib/csrc'")

  ' 1. Create a tiny C + VerBose library
  VpmWriteFile($"{root}/mylib/csrc/add.c", "int mylib_c_add(int a, int b) { return a + b; }\n")
  VpmWriteFile($"{root}/mylib/src/mylib.vb", "Declare Function mylib_c_add(a, b As Integer) As Integer\nPublic Function MyLibDoubleAdd(a, b As Integer) As Integer\n  Return mylib_c_add(a, b) * 2\nEnd Function\n")
  VpmWriteFile($"{root}/mylib/verbose.toml", "[package]\nname = \"mylib\"\nversion = \"0.1.0\"\n\n[lib]\ninclude_dir = \"src\"\nentry = \"mylib.vb\"\nc_sources = [\"csrc/add.c\"]\nc_libs = []\n")

  ' Resolve vpm binary path via VPM_BIN env var (set by makefile)
  Dim vpmEnv As Pointer = getenv("VPM_BIN")
  Dim vpmBin As String = "vpm"
  If Not IsNull(vpmEnv) Then
    vpmBin = vpmEnv
  End If

  ' 2. Pack mylib as a binary .vpkg.tar.gz bundle using vpm
  Assert VpmExec($"cd '{root}/mylib' && '{vpmBin}' pack --binary") = 0, "vpm pack --binary failed"
  Assert VpmFileExists($"{root}/mylib/dist/mylib-0.1.0-bin.vpkg.tar.gz"), "packed archive missing"

  ' 3. Scaffold a fresh consumer app, add the packed archive, and run it!
  Assert VpmExec($"cd '{root}' && '{vpmBin}' init myapp") = 0, "vpm init failed"
  Assert VpmExec($"cd '{root}/myapp' && '{vpmBin}' add mylib '{root}/mylib/dist/mylib-0.1.0-bin.vpkg.tar.gz'") = 0, "vpm add archive failed"

  Dim appCode As String = "Include \"mylib.vb\"\nPublic Function main() As Integer\n  Dim r As Integer = MyLibDoubleAdd(10, 11)\n  If r = 42 Then\n    Return 0\n  End If\n  Return 1\nEnd Function\n"
  VpmWriteFile($"{root}/myapp/src/main.vb", appCode)

  Assert VpmExec($"cd '{root}/myapp' && '{vpmBin}' run") = 0, "vpm run failed on consumer app"

  VpmExec($"rm -rf '{root}'")
  ArenaDestroy(ByRef a)
End Test
