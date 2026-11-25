' verbose-pkg (vpm) — Zero-Dependency `verbose.toml` Manifest Parser (with [workspace] support)
Include "util.vb"

Public Enum DepKind
  DEP_NONE = 0
  DEP_GIT = 1
  DEP_PATH = 2
  DEP_ARCHIVE = 3
End Enum

Public Structure DepSpec
  Dim name, source, branch As String
  Dim kind As Integer
End Structure

Public Structure Manifest
  ' [package]
  Dim name, version, description, entry, license, repository As String
  ' [lib]
  Dim includeDir, libEntry, prebuilt, buildCmd As String
  Dim cSources(16) As String
  Dim cSourceCount As Integer
  Dim cLibs(16) As String
  Dim cLibCount As Integer
  ' [dependencies]
  Dim deps(32) As DepSpec
  Dim depCount As Integer
  ' [workspace]
  Dim members(32) As String
  Dim memberCount As Integer
End Structure

Private Sub ParseStringArray(ByRef a As Arena, val As String, ByRef outArr() As String, ByRef outCount As Integer, maxItems As Integer)
  Dim n As Integer = VpmStrLen(val)
  Dim i As Integer = 0
  While i < n And outCount < maxItems Do
    If val(i) = 34 Then
      Dim lo As Integer = i + 1
      i = i + 1
      While i < n And val(i) <> 34 Do
        i = i + 1
      End While
      outArr(outCount) = VpmDupRange(ByRef a, val, lo, i - lo)
      outCount = outCount + 1
    End If
    i = i + 1
  End While
End Sub

Private Sub ParseDepLine(ByRef a As Arena, depName, val As String, ByRef d As DepSpec)
  With d
    .name = depName
    .source = ""
    .branch = ""
    .kind = DEP_NONE
  End With

  Dim gitIdx As Integer = VpmIndexOf(val, "git")
  Dim pathIdx As Integer = VpmIndexOf(val, "path")
  Dim archIdx As Integer = VpmIndexOf(val, "archive")
  Dim brIdx As Integer = VpmIndexOf(val, "branch")
  Dim tagIdx As Integer = VpmIndexOf(val, "tag")

  If gitIdx >= 0 Then
    d.kind = DEP_GIT
    d.source = VpmExtractQuoted(ByRef a, val, gitIdx)
    If tagIdx >= 0 Then
      d.branch = VpmExtractQuoted(ByRef a, val, tagIdx)
    Else
      If brIdx >= 0 Then
        d.branch = VpmExtractQuoted(ByRef a, val, brIdx)
      End If
    End If
  Else
    If pathIdx >= 0 Then
      d.kind = DEP_PATH
      d.source = VpmExtractQuoted(ByRef a, val, pathIdx)
    Else
      If archIdx >= 0 Then
        d.kind = DEP_ARCHIVE
        d.source = VpmExtractQuoted(ByRef a, val, archIdx)
      Else
        ' Shorthand: dep = "path/or/url"
        Dim raw As String = VpmExtractQuoted(ByRef a, val, 0)
        d.source = raw
        If VpmStartsWith(raw, "http://") Or VpmStartsWith(raw, "https://") Or VpmStartsWith(raw, "git@") Then
          If VpmEndsWith(raw, ".tar.gz") Or VpmEndsWith(raw, ".tgz") Then
            d.kind = DEP_ARCHIVE
          Else
            d.kind = DEP_GIT
          End If
        Else
          If VpmEndsWith(raw, ".tar.gz") Or VpmEndsWith(raw, ".tgz") Then
            d.kind = DEP_ARCHIVE
          Else
            d.kind = DEP_PATH
          End If
        End If
      End If
    End If
  End If
End Sub

Public Function ParseManifest(ByRef a As Arena, path As String, ByRef m As Manifest) As Boolean
  Dim text As String = VpmReadFile(ByRef a, path)
  If IsNull(text) Then
    Return False
  End If

  With m
    .name = "app"
    .version = "0.1.0"
    .description = ""
    .entry = "src/main.vb"
    .license = "MIT"
    .repository = ""
    .includeDir = "src"
    .libEntry = ""
    .prebuilt = ""
    .buildCmd = ""
    .cSourceCount = 0
    .cLibCount = 0
    .depCount = 0
    .memberCount = 0
  End With

  Dim section As Integer = 0 ' 1=package, 2=lib, 3=dependencies, 4=workspace
  Dim n As Integer = VpmStrLen(text)
  Dim pos As Integer = 0

  While pos < n Do
    Dim lineStart As Integer = pos
    While pos < n And text(pos) <> 10 Do
      pos = pos + 1
    End While
    Dim rawLine As String = VpmDupRange(ByRef a, text, lineStart, pos - lineStart)
    pos = pos + 1

    ' Strip `#` comment outside quotes
    Dim lineLen As Integer = VpmStrLen(rawLine)
    Dim inQ As Boolean = False
    Dim cut As Integer = lineLen
    For i = 0 To lineLen - 1 Do
      If rawLine(i) = 34 Then
        inQ = Not inQ
      End If
      If rawLine(i) = 35 And Not inQ And cut = lineLen Then
        cut = i
      End If
    End For
    Dim clean As String = VpmTrim(ByRef a, VpmDupRange(ByRef a, rawLine, 0, cut))
    Dim clen As Integer = VpmStrLen(clean)

    If clen > 0 Then
      If clean(0) = 91 Then
        ' Section header [package], [lib], [dependencies], [workspace]
        Match clean With
          Case "[package]" Then section = 1
          Case "[lib]" Then section = 2
          Case "[dependencies]" Then section = 3
          Case "[workspace]" Then section = 4
          Case _ Then section = 0
        End Match
      Else
        Dim eqPos As Integer = VpmIndexOf(clean, "=")
        If eqPos > 0 Then
          Dim k As String = VpmTrim(ByRef a, VpmDupRange(ByRef a, clean, 0, eqPos))
          Dim v As String = VpmTrim(ByRef a, VpmDupRange(ByRef a, clean, eqPos + 1, clen - eqPos - 1))

          Match section With
            Case 1 Then
              Match k With
                Case "name" Then m.name = VpmExtractQuoted(ByRef a, v, 0)
                Case "version" Then m.version = VpmExtractQuoted(ByRef a, v, 0)
                Case "description" Then m.description = VpmExtractQuoted(ByRef a, v, 0)
                Case "entry" Then m.entry = VpmExtractQuoted(ByRef a, v, 0)
                Case "license" Then m.license = VpmExtractQuoted(ByRef a, v, 0)
                Case "repository" Then m.repository = VpmExtractQuoted(ByRef a, v, 0)
                Case _ Then
              End Match
            Case 2 Then
              Match k With
                Case "include_dir" Then m.includeDir = VpmExtractQuoted(ByRef a, v, 0)
                Case "entry" Then m.libEntry = VpmExtractQuoted(ByRef a, v, 0)
                Case "prebuilt" Then m.prebuilt = VpmExtractQuoted(ByRef a, v, 0)
                Case "build_cmd" Then m.buildCmd = VpmExtractQuoted(ByRef a, v, 0)
                Case "c_sources" Then ParseStringArray(ByRef a, v, ByRef m.cSources, ByRef m.cSourceCount, 16)
                Case "c_libs" Then ParseStringArray(ByRef a, v, ByRef m.cLibs, ByRef m.cLibCount, 16)
                Case _ Then
              End Match
            Case 3 Then
              If m.depCount < 32 Then
                ParseDepLine(ByRef a, k, v, ByRef m.deps(m.depCount))
                m.depCount = m.depCount + 1
              End If
            Case 4 Then
              Match k With
                Case "members" Then ParseStringArray(ByRef a, v, ByRef m.members, ByRef m.memberCount, 32)
                Case _ Then
              End Match
            Case _ Then
          End Match
        End If
      End If
    End If
  End While

  Return True
End Function
