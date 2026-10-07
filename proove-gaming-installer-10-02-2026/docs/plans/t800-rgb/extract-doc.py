"""Extract the text of a legacy Word 97 (.doc) file.

Used to translate `docs/ry-yc3123-机械轴__2025_07_18.doc` into
`docs/knowledge-base/rongyuan-yc3123-gaming-protocol.md`. Kept so the translation can be
re-checked against the original without hunting for a converter.

That file is an OLE2 compound document, not a zipped .docx, so python-docx cannot read it and
there is no Word or LibreOffice on the build machines. This walks the FIB piece table directly.

    python -m pip install olefile
    python docs/plans/t800-rgb/extract-doc.py "docs/ry-yc3123-机械轴__2025_07_18.doc" > out.txt

Table cell boundaries come out as `\t|\t`, which is enough structure to read the protocol tables.
"""

import olefile, struct, sys

path = sys.argv[1]
o = olefile.OleFileIO(path)
wd = o.openstream('WordDocument').read()

flags = struct.unpack_from('<H', wd, 0x0A)[0]
table_name = '1Table' if (flags & 0x0200) else '0Table'
if not o.exists(table_name):
    table_name = '0Table' if o.exists('0Table') else '1Table'
tbl = o.openstream(table_name).read()

fcMin = struct.unpack_from('<i', wd, 0x18)[0]
fcClx, lcbClx = struct.unpack_from('<ii', wd, 0x01A2)
clx = tbl[fcClx:fcClx + lcbClx]

# walk Clx to find the Pcdt (0x02)
i = 0
pcdt = None
while i < len(clx):
    t = clx[i]
    if t == 1:
        cb = struct.unpack_from('<H', clx, i + 1)[0]
        i += 3 + cb
    elif t == 2:
        lcb = struct.unpack_from('<I', clx, i + 1)[0]
        pcdt = clx[i + 5:i + 5 + lcb]
        break
    else:
        break

n = (len(pcdt) - 4) // 12
cps = [struct.unpack_from('<I', pcdt, 4 * k)[0] for k in range(n + 1)]
out = []
for k in range(n):
    off = 4 * (n + 1) + 8 * k
    fc = struct.unpack_from('<I', pcdt, off + 2)[0]
    compressed = bool(fc & 0x40000000)
    fc &= 0x3FFFFFFF
    ncp = cps[k + 1] - cps[k]
    if compressed:
        raw = wd[fc // 2: fc // 2 + ncp]
        out.append(raw.decode('cp1252', 'replace'))
    else:
        raw = wd[fc: fc + ncp * 2]
        out.append(raw.decode('utf-16-le', 'replace'))

text = ''.join(out)
text = text.replace('\r', '\n').replace('\x07', '\t|\t').replace('\x0b', '\n')
sys.stdout.reconfigure(encoding='utf-8')
print(text)
