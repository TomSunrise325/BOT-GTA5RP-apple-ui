import sys, struct
sys.path.insert(0, '_tools/libs')
import pefile, capstone

path = sys.argv[1]
pe = pefile.PE(path)
d = pe.__data__
text = [s for s in pe.sections if s.Name.startswith(b'.text')][0]
t0, tl = text.PointerToRawData, text.Misc_VirtualSize
tx = d[t0:t0 + tl]
base_rva = text.VirtualAddress

# valid function ranges from .pdata
pd = [s for s in pe.sections if s.Name.startswith(b'.pdata')][0]
raw = d[pd.PointerToRawData:pd.PointerToRawData + pd.Misc_VirtualSize]
funcs = []
for i in range(0, len(raw) - 11, 12):
    b, e, u = struct.unpack('<III', raw[i:i + 12])
    if b == 0: break
    funcs.append((b, e))
print('functions:', len(funcs))

md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.skipdata = True

sites = []
for b, e in funcs:
    off = b - base_rva
    code = tx[off:off + (e - b)]
    # linear sweep with capstone; "(bad)"/skipdata entries at 0x8F mark XOP
    pos = 0
    while pos < len(code):
        ins = next(md.disasm(code[pos:pos + 16], b + pos, 1), None)
        if ins is None:
            if code[pos] == 0x8f:
                sites.append((b + pos, bytes(code[pos:pos + 16])))
            pos += 1; continue
        if ins.bytes[0] == 0x8f and ins.mnemonic != 'pop':
            sites.append((b + pos, bytes(ins.bytes), ins.mnemonic + ' ' + ins.op_str))
        pos += ins.size

print('suspect XOP sites:', len(sites))
for rva, bs, txt in sites:
    print(hex(rva), len(bs), bs.hex(' '), '|', txt)
