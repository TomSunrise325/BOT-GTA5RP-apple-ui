"""Check every patched site: run the original TBM/XOP semantics and the replacement on random inputs."""
import sys, struct, random, re
sys.path.insert(0, '_tools/libs')
import pefile, capstone

orig_pe = pefile.PE(sys.argv[1]); new_pe = pefile.PE(sys.argv[2])
od, nd = orig_pe.__data__, new_pe.__data__
ot = [s for s in orig_pe.sections if s.Name.startswith(b'.text')][0]
nt = [s for s in new_pe.sections if s.Name.startswith(b'.text')][0]
cave = [s for s in new_pe.sections if s.Name.startswith(b'.cave')][0]
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = True
pdsec = [s for s in orig_pe.sections if s.Name.startswith(b'.pdata')][0]
raw = od[pdsec.PointerToRawData:pdsec.PointerToRawData + pdsec.Misc_VirtualSize]
funcs = [struct.unpack('<II', raw[i:i + 8]) for i in range(0, len(raw) - 11, 12)]

def rd(pe, sec, rva, n):
    o = sec.PointerToRawData + rva - sec.VirtualAddress
    return pe.__data__[o:o + n]

R64 = ['rax','rcx','rdx','rbx','rsp','rbp','rsi','rdi'] + ['r%d' % i for i in range(8, 16)]
R32 = ['eax','ecx','edx','ebx','esp','ebp','esi','edi'] + ['r%dd' % i for i in range(8, 16)]
def reg(name):
    if name in R64: return R64.index(name), 64
    if name in R32: return R32.index(name), 32
    raise KeyError(name)

M64 = (1 << 64) - 1
def setreg(st, name, v):
    i, w = reg(name)
    st[i] = (v & 0xFFFFFFFF) if w == 32 else (v & M64)   # 32-bit writes zero-extend
def getreg(st, name):
    i, w = reg(name)
    return st[i] & (0xFFFFFFFF if w == 32 else M64)

def run_cave(rva_start, st, mem):
    """interpret mov/shr/and/btr/lea/andn until the final jmp; returns next rva"""
    rva = rva_start
    for _ in range(12):
        ins = next(md.disasm(rd(new_pe, cave, rva, 16), rva, 1))
        m, ops = ins.mnemonic, [o.strip() for o in ins.op_str.split(',')]
        if m == 'jmp': return int(ops[0], 16) - 0x140000000 if int(ops[0], 16) > 0x140000000 else int(ops[0], 16)
        if m == 'mov':
            if '[' in ops[1]:
                addr = ops[1]; setreg(st, ops[0], mem[addr]) if addr in mem else (_ for _ in ()).throw(KeyError(addr))
            else: setreg(st, ops[0], getreg(st, ops[1]))
        elif m == 'shr':  setreg(st, ops[0], getreg(st, ops[0]) >> int(ops[1], 0))
        elif m == 'and':  setreg(st, ops[0], getreg(st, ops[0]) & int(ops[1], 0))
        elif m == 'btr':  setreg(st, ops[0], getreg(st, ops[0]) & ~(1 << int(ops[1], 0)))
        elif m == 'lea':
            mm = re.match(r'\[(\w+) \+ (0x[0-9a-f]+|\d+)\]', ops[1]); setreg(st, ops[0], getreg(st, mm.group(1)) + int(mm.group(2), 0))
        elif m == 'andn': setreg(st, ops[0], (~getreg(st, ops[1]) & getreg(st, ops[2])))
        else: raise SystemExit('unknown mnemonic in cave: %s %s' % (m, ins.op_str))
        rva += ins.size
    raise SystemExit('cave runs too long')

ok = bad = 0
for b, e in funcs:
    code = rd(orig_pe, ot, b, e - b)
    pos = 0
    while pos < len(code):
        ins = next(md.disasm(code[pos:pos + 16], b + pos, 1), None)
        if ins is None: pos += 1; continue
        site = b + pos
        if ins.bytes[0] == 0x8f and ins.mnemonic in ('bextr', 'blcic'):
            new_first = rd(new_pe, nt, site, 5)
            assert new_first[0] == 0xE9, 'site %x not redirected' % site
            target = site + 5 + struct.unpack('<i', new_first[1:5])[0]
            ops = [o.strip() for o in ins.op_str.split(',')]
            for _ in range(200):
                st = [random.getrandbits(64) for _ in range(16)]
                mem = {}
                if ins.mnemonic == 'bextr':
                    imm = int(ops[2], 0); start, ln = imm & 0xFF, (imm >> 8) & 0xFF
                    if '[' in ops[1]:
                        val = random.getrandbits(32); mem[ops[1]] = val; src = val
                    else: src = getreg(st, ops[1])
                    bits = 64 if reg(ops[0])[1] == 64 else 32
                    exp = (src >> start) & ((1 << min(ln, bits)) - 1) if start < bits else 0
                else:
                    src = getreg(st, ops[1]); exp = (~src & (src + 1)) & 0xFFFFFFFF
                st2 = list(st)
                nxt = run_cave(target, st2, mem)
                got = getreg(st2, ops[0])
                if got != exp or nxt != site + ins.size:
                    bad += 1; print('MISMATCH at %x: %s %s exp=%x got=%x next=%x' % (site, ins.mnemonic, ins.op_str, exp, got, nxt)); break
            else: ok += 1
        pos += ins.size
print('verified sites OK:', ok, ' mismatches:', bad)

# vpcmpeqq in place + vpperm caves: decode check
for rva in (0x23062af, 0x23062b9, 0x23062c3, 0x23062cd, 0x230664f, 0x2306659, 0x2306663, 0x230666d):
    i = next(md.disasm(rd(new_pe, nt, rva, 16), rva, 1)); o = next(md.disasm(rd(orig_pe, ot, rva, 16), rva, 1))
    assert i.mnemonic == 'vpcmpeqq' and i.op_str == o.op_str, (hex(rva), i.mnemonic, i.op_str, o.op_str)
print('vpcomeqq -> vpcmpeqq: 8 sites, same operands')
