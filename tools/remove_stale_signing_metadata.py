from pathlib import Path

path = Path("OmenSuperHub.csproj")
raw = path.read_bytes()
had_bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")
use_crlf = "\r\n" in text
text = text.replace("\r\n", "\n")

blocks = [
'''  <PropertyGroup>
    <ManifestCertificateThumbprint>0FF25508E82D933D2889D9605B7F06A55C7283BB</ManifestCertificateThumbprint>
  </PropertyGroup>
''',
'''  <PropertyGroup>
    <ManifestKeyFile>OmenSuperHub_TemporaryKey.pfx</ManifestKeyFile>
  </PropertyGroup>
''',
'''    <None Include="OmenSuperHub_TemporaryKey.pfx" />
'''
]

for block in blocks:
    if block not in text:
        raise SystemExit("expected stale signing metadata not found")
    text = text.replace(block, "", 1)

if use_crlf:
    text = text.replace("\n", "\r\n")
data = text.encode("utf-8")
if had_bom:
    data = b"\xef\xbb\xbf" + data
path.write_bytes(data)
