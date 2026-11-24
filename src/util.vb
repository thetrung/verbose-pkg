' verbose-pkg (vpm) — Core String, File & Process Utilities (Pure VerBose-Lang)

Declare Function fopen(path, mode As String) As Pointer
Declare Function fclose(fp As Pointer) As Integer
Declare Function fseek(fp As Pointer, offset As Long, whence As Integer) As Integer
Declare Function ftell(fp As Pointer) As Long
Declare Function fread(buf As Pointer, sz, count As Long, fp As Pointer) As Long
Declare Function fwrite(buf As Pointer, sz, count As Long, fp As Pointer) As Long
Declare Function system(cmd As String) As Integer
Declare Function getenv(name As String) As Pointer
Declare Function strlen(s As String) As Long
Declare Function strcmp(a, b As String) As Integer
Declare Function strncmp(a, b As String, n As Long) As Integer
Declare Function memcpy(dst, src As Pointer, n As Long) As Pointer
Declare Function memset(dst As Pointer, c As Integer, n As Long) As Pointer

Public Function VpmStrLen(s As String) As Integer
  If IsNull(s) Then
    Return 0
  End If
  Dim n As Integer = 0
  While s(n) <> 0 Do
    n = n + 1
  End While
  Return n
End Function

Public Function VpmStrEq(a, b As String) As Boolean
  If IsNull(a) Or IsNull(b) Then
    Return False
  End If
  Return strcmp(a, b) = 0
End Function

Public Function VpmStartsWith(s, prefix As String) As Boolean
  If IsNull(s) Or IsNull(prefix) Then
    Return False
  End If
  Dim plen As Integer = VpmStrLen(prefix)
  Dim slen As Integer = VpmStrLen(s)
  If slen < plen Then
    Return False
  End If
  For i = 0 To plen - 1 Do
    If s(i) <> prefix(i) Then
      Return False
    End If
  End For
  Return True
End Function

Public Function VpmEndsWith(s, suffix As String) As Boolean
  If IsNull(s) Or IsNull(suffix) Then
    Return False
  End If
  Dim slen As Integer = VpmStrLen(s)
  Dim flen As Integer = VpmStrLen(suffix)
  If slen < flen Then
    Return False
  End If
  For i = 0 To flen - 1 Do
    If s(slen - flen + i) <> suffix(i) Then
      Return False
    End If
  End For
  Return True
End Function

Public Function VpmContainsChar(s As String, ch As Byte) As Boolean
  If IsNull(s) Then
    Return False
  End If
  Dim i As Integer = 0
  While s(i) <> 0 Do
    If s(i) = ch Then
      Return True
    End If
    i = i + 1
  End While
  Return False
End Function

Public Function VpmDupRange(ByRef a As Arena, src As String, startIdx, length As Integer) As String
  If length < 0 Then
    length = 0
  End If
  Dim buf As Pointer = ArenaAllocBytes(ByRef a, length + 1)
  For i = 0 To length - 1 Do
    buf(i) = src(startIdx + i)
  End For
  buf(length) = 0
  Return buf
End Function

Public Function VpmTrim(ByRef a As Arena, s As String) As String
  If IsNull(s) Then
    Return ""
  End If
  Dim n As Integer = VpmStrLen(s)
  Dim lo As Integer = 0
  While lo < n And (s(lo) = 32 Or s(lo) = 9 Or s(lo) = 13 Or s(lo) = 10) Do
    lo = lo + 1
  End While
  Dim hi As Integer = n
  While hi > lo And (s(hi - 1) = 32 Or s(hi - 1) = 9 Or s(hi - 1) = 13 Or s(hi - 1) = 10) Do
    hi = hi - 1
  End While
  Return VpmDupRange(ByRef a, s, lo, hi - lo)
End Function

Public Function VpmFileExists(path As String) As Boolean
  Dim fp As Pointer = fopen(path, "rb")
  If IsNull(fp) Then
    Return False
  End If
  fclose(fp)
  Return True
End Function

Public Function VpmReadFile(ByRef a As Arena, path As String) As String
  Dim fp As Pointer = fopen(path, "rb")
  If IsNull(fp) Then
    Return 0
  End If
  fseek(fp, 0, 2)
  Dim sz As Long = ftell(fp)
  fseek(fp, 0, 0)
  Dim n As Integer = sz
  Dim buf As Pointer = ArenaAllocBytes(ByRef a, n + 1)
  If IsNull(buf) Then
    fclose(fp)
    Return 0
  End If
  If n > 0 Then
    fread(buf, 1, sz, fp)
  End If
  buf(n) = 0
  fclose(fp)
  Return buf
End Function

Public Function VpmWriteFile(path, content As String) As Boolean
  Dim fp As Pointer = fopen(path, "wb")
  If IsNull(fp) Then
    Return False
  End If
  Dim n As Long = VpmStrLen(content)
  If n > 0 Then
    fwrite(content, 1, n, fp)
  End If
  fclose(fp)
  Return True
End Function

Public Function VpmAppendFile(path, content As String) As Boolean
  Dim fp As Pointer = fopen(path, "ab")
  If IsNull(fp) Then
    Return False
  End If
  Dim n As Long = VpmStrLen(content)
  If n > 0 Then
    fwrite(content, 1, n, fp)
  End If
  fclose(fp)
  Return True
End Function

Public Function VpmExec(cmd As String) As Integer
  Dim rc As Integer = system(cmd)
  Return rc
End Function

' Extract quoted value from a TOML snippet starting at `pos` (pointing at or before `"`)
Public Function VpmExtractQuoted(ByRef a As Arena, s As String, startIdx As Integer) As String
  Dim n As Integer = VpmStrLen(s)
  Dim i As Integer = startIdx
  While i < n And s(i) <> 34 Do
    i = i + 1
  End While
  If i >= n Then
    Return ""
  End If
  i = i + 1
  Dim lo As Integer = i
  While i < n And s(i) <> 34 Do
    i = i + 1
  End While
  Return VpmDupRange(ByRef a, s, lo, i - lo)
End Function

' Find substring `needle` in `haystack`; returns index or -1
Public Function VpmIndexOf(haystack, needle As String) As Integer
  Dim hlen As Integer = VpmStrLen(haystack)
  Dim nlen As Integer = VpmStrLen(needle)
  If nlen = 0 Then
    Return 0
  End If
  If nlen > hlen Then
    Return -1
  End If
  For i = 0 To hlen - nlen Do
    Dim ok As Boolean = True
    For j = 0 To nlen - 1 Do
      If haystack(i + j) <> needle(j) Then
        ok = False
      End If
    End For
    If ok Then
      Return i
    End If
  End For
  Return -1
End Function
