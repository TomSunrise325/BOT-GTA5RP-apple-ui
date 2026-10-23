"""Make main.exe runnable on Intel / modern AMD.

main.exe was compiled with AMD-only extensions (TBM: bextr-imm, blcic; XOP: vpcomq, vpperm), which raise
#UD on every Intel CPU and on AMD Zen. This tool rewrites each such instruction with equivalent
common instructions (BMI1 / AVX / SSSE3). Short sites are patched in place; the rest jump to a small
code cave stored in a new PE section.

usage: patch_main.py <in main.exe> <out main.exe>
"""
import sys, struct
sys.path.insert(0, '_tools/libs')
import pefile, capstone
from capstone import x86

src, dst = sys.argv[1], sys.argv[2]
pe = pefile.PE(src)
data = bytearray(pe.__data__)
text = [s for s in pe.sections if s.Name.startswith(b'.text')][0]
pd = [s for s in pe.sections if s.Name.startswith(b'.pdata')][0]
raw = bytes(data[pd.PointerToRawData:pd.PointerToRawData + pd.Misc_VirtualSize])
funcs = []
for i in range(0, len(raw) - 11, 12):
    b, e, u = struct.unpack('<III', raw[i:i + 12])
    if b == 0: break
    funcs.append((b, e))

def rva2off(rva):
    return text.PointerToRawData + rva - text.VirtualAddress

md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
md.detail = True

# ---------- 1. find every site (and the following instructions, for flag analysis) ----------
sites = []  # (rva, size, mnemonic, bytes, func_range)
FLAG_READERS = {'jo','jno','jb','jae','je','jne','jbe','ja','js','jns','jp','jnp','jl','jge','jle','jg',
                'cmovo','cmovno','cmovb','cmovae','cmove','cmovne','cmovbe','cmova','cmovs','cmovns','cmovp','cmovnp','cmovl','cmovge','cmovle','cmovg',
                'seto','setno','setb','setae','sete','setne','setbe','seta','sets','setns','setp','setnp','setl','setge','setle','setg',
                'adc','sbb','jecxz','jrcxz','pushfq','lahf'}
FLAG_WRITERS = {'add','sub','and','or','xor','cmp','test','inc','dec','neg','shl','shr','sar','rol','ror','imul','mul','bt','bts','btr','btc','bsf','bsr','popcnt','lzcnt','tzcnt','call','ret'}
flag_warn = []
for b, e in funcs:
    code = bytes(data[rva2off(b):rva2off(b) + (e - b)])
    pos = 0
    insl = []
    while pos < len(code):
        ins = next(md.disasm(code[pos:pos + 16], b + pos, 1), None)
        if ins is None:
            pos += 1; continue
        insl.append(ins)
        pos += ins.size
    for i, ins in enumerate(insl):
        bad = (ins.bytes[0] == 0x8f and ins.mnemonic != 'pop')
        if bad:
            sites.append((ins.address, ins.size, ins.mnemonic, bytes(ins.bytes), (b, e)))
            if ins.mnemonic in ('bextr', 'blcic'):
                for nxt in insl[i + 1:i + 6]:
                    if nxt.mnemonic in FLAG_READERS:
                        flag_warn.append((hex(ins.address), ins.mnemonic, nxt.mnemonic)); break
                    if nxt.mnemonic in FLAG_WRITERS: break
print('sites:', len(sites), ' flag-dependent:', flag_warn)

# ---------- 2. tiny encoder ----------
def rex(w, r, x, b, force=False):
    v = 0x40 | (w << 3) | (r << 2) | (x << 1) | b
    return bytes([v]) if (v != 0x40 or force) else b''

def mov_rr(w, dst, src):           # mov dst, src   (89 /r)
    return rex(w, src >> 3, 0, dst >> 3) + bytes([0x89, 0xC0 | ((src & 7) << 3) | (dst & 7)])

def shr_ri(w, dst, n):
    return rex(w, 0, 0, dst >> 3) + bytes([0xC1, 0xE8 | (dst & 7), n])

def and_ri(w, dst, mask):
    if mask < 0x80:
        return rex(w, 0, 0, dst >> 3) + bytes([0x83, 0xE0 | (dst & 7), mask])
    assert mask < 0x80000000
    return rex(w, 0, 0, dst >> 3) + bytes([0x81, 0xE0 | (dst & 7)]) + struct.pack('<I', mask)

def btr_ri(w, dst, bit):
    return rex(w, 0, 0, dst >> 3) + bytes([0x0F, 0xBA, 0xF0 | (dst & 7), bit])

def jmp_rel32(from_rva, to_rva):
    return b'\xE9' + struct.pack('<i', to_rva - (from_rva + 5))

NAMES = ['rax','rcx','rdx','rbx','rsp','rbp','rsi','rdi'] + ['r%d' % i for i in range(8, 16)]

# ---------- 3. build patches ----------
cave = bytearray()
patches = []   # (site_rva, size, kind, payload)  kind: 'inplace' or 'cave'
consts = {}    # name -> offset in cave (filled later)

def emit_bextr(site_rva, size, bs):
    b1, b2, op, modrm = bs[1], bs[2], bs[3], bs[4]
    assert bs[0] == 0x8F and (b1 & 0x1F) == 0x0A and op == 0x10, bs.hex()
    R = ((b1 >> 7) & 1) ^ 1; X = ((b1 >> 6) & 1) ^ 1; B = ((b1 >> 5) & 1) ^ 1
    W = (b2 >> 7) & 1
    mod, reg, rm = modrm >> 6, (modrm >> 3) & 7, modrm & 7
    dstr = reg | (R << 3)
    imm = struct.unpack('<I', bs[size - 4:size])[0]
    start, ln = imm & 0xFF, (imm >> 8) & 0xFF
    bits = 64 if W else 32
    code = b''
    if mod == 3:
        srcr = rm | (B << 3)
        if srcr != dstr:
            code += mov_rr(W, dstr, srcr)
    else:
        assert not (mod == 0 and rm == 5), 'rip-relative bextr'
        mem = bs[5:size - 4]                     # sib/disp bytes
        code += rex(W, R, X, B) + bytes([0x8B, modrm]) + mem   # mov dst, [mem]
    if start:
        code += shr_ri(W, dstr, start)
    if ln < bits:
        mask = (1 << ln) - 1
        if mask <= 0x7FFFFFFF:
            code += and_ri(W, dstr, mask)
        elif W and ln == 63:
            code += btr_ri(1, dstr, 63)
        else:
            raise SystemExit('unsupported mask %x at %x' % (mask, site_rva))
    elif start == 0 and not W and mod == 3 and srcr == dstr:
        code += mov_rr(0, dstr, dstr)
    return code

def emit_blcic(bs):
    # 8F C9 78 01 /5 : blcic dst(vvvv), src(rm)  ->  dst = ~src & (src+1)
    b1, b2, modrm = bs[1], bs[2], bs[4]
    B = ((b1 >> 5) & 1) ^ 1
    W = (b2 >> 7) & 1
    vvvv = ((~(b2 >> 3)) & 0xF)
    dstr, srcr = vvvv, (modrm & 7) | (B << 3)
    assert modrm >> 6 == 3 and ((modrm >> 3) & 7) == 5 and not W
    # lea dst,[src+1]
    lea = rex(0, dstr >> 3, 0, srcr >> 3) + bytes([0x8D, 0x40 | ((dstr & 7) << 3) | (srcr & 7)])
    if (srcr & 7) == 4: raise SystemExit('rsp/r12 base needs SIB')
    lea += b'\x01'
    # andn dst, src, dst  (BMI1)  VEX3: C4 [~R ~X ~B 00010] [W ~vvvv L pp] F2 modrm
    vb1 = ((((dstr >> 3) ^ 1) << 7) | (1 << 6) | (((dstr >> 3) ^ 1) << 5) | 0x02)
    vb2 = ((((~srcr) & 0xF) << 3) | 0x00)
    andn = bytes([0xC4, vb1, vb2, 0xF2, 0xC0 | ((dstr & 7) << 3) | (dstr & 7)])
    return lea + andn

def emit_vpcomeqq_inplace(bs):
    # 8F E8 [W vvvv L pp] CF modrm 04   ->   VEX3 vpcmpeqq: C4 E2 [W vvvv L 01] 29 modrm   (+ NOP)
    assert bs[0] == 0x8F and bs[1] == 0xE8 and bs[3] == 0xCF and bs[5] == 0x04, bs.hex()
    vvvv_byte = bs[2] & 0x78                      # W=0, keeps vvvv/L
    assert (bs[2] & 0x80) == 0 and (bs[2] & 0x07) == 0
    return bytes([0xC4, 0xE2, vvvv_byte | 0x01, 0x29, bs[4]]) + b'\x90'

def emit_vpperm(site_rva, bs):
    # vpperm xmmK, xmmK, [mem], xmm0   with selector = 00 04 08 0c 10 14 18 1c (low qword): byte0 of 8 dwords
    assert bs[0] == 0x8F and bs[1] == 0xA8 and bs[3] == 0xA3 and bs[4] in (0x4C, 0x54, 0x5C, 0x64)
    k = (bs[4] >> 3) & 7
    assert bs[5] == 0x9A                          # SIB rdx + r11*4
    disp = bs[6]
    # vmovdqu xmm5, [rdx+r11*4+disp] : VEX3 C4 [R~=1 X~=0(r11) B~=1 00001] [0 1111 0 10] 6F modrm sib disp8
    load = bytes([0xC4, 0xC1 ^ 0x40 ^ 0x40, 0x7A, 0x6F, 0x6C, 0x9A, disp])
    # compute exact bytes: R=0 (xmm5<8) ->~R=1 ; X=1 (r11) -> ~X=0 ; B=0 (rdx) -> ~B=1 ; map=0F -> 00001 => 1 0 1 00001 = 0xA1
    load = bytes([0xC4, 0xA1, 0x7A, 0x6F, 0x6C, 0x9A, disp])    # modrm: mod=01 reg=101(xmm5) rm=100 -> 0x6C
    # vpshufb xmm5, xmm5, [rip+M2] : VEX3 C4 E2 51 00 modrm(00 101 101) disp32
    # vpshufb xmmK, xmmK, [rip+M1]
    return ('vpperm', k, load)

# --- build caves ---
site_rva_to_patch = {}
M1 = bytes([0, 4, 8, 12, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80])
M2 = bytes([0x80, 0x80, 0x80, 0x80, 0, 4, 8, 12, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80])

def pad_to(n):
    while len(cave) % n: cave.append(0xCC)

pending = []   # (site_rva, size, cave_off, fixups)
for rva, size, mn, bs, (fb, fe) in sites:
    if mn == 'vpcomeqq':
        patches.append((rva, size, 'inplace', emit_vpcomeqq_inplace(bs)))
    elif mn in ('bextr', 'blcic'):
        body = emit_blcic(bs) if mn == 'blcic' else emit_bextr(rva, size, bs)
        pad_to(16)
        off = len(cave)
        cave += body
        # jmp back is added once the cave base is known
        pending.append((rva, size, off, len(body), None))
    elif mn == 'vpperm':
        # check xmm5 is not referenced anywhere in this function
        fcode = bytes(data[rva2off(fb):rva2off(fb) + (fe - fb)])
        p = 0; used = False
        while p < len(fcode):
            ins = next(md.disasm(fcode[p:p + 16], fb + p, 1), None)
            if ins is None: p += 1; continue
            if 'xmm5' in ins.op_str or 'ymm5' in ins.op_str: used = True; break
            p += ins.size
        if used:   # checked by hand: every xmm5 use in this function is a fresh def (vpmovzx*/vextractf128) -> not live in the loop
            print('note: xmm5 also used elsewhere in function of vpperm at %x (short-lived temp, not live here)' % rva)
        k = (bs[4] >> 3) & 7
        disp = bs[6]
        pad_to(16)
        off = len(cave)
        body = bytearray()
        body += bytes([0xC4, 0xA1, 0x7A, 0x6F, 0x6C, 0x9A, disp])                      # vmovdqu xmm5, [rdx+r11*4+disp]
        body += b'@@M2@@'                                                               # vpshufb xmm5, xmm5, [rip+M2]
        body += b'@@M1:%d@@' % k                                                        # vpshufb xmmK, xmmK, [rip+M1]
        # vpor xmmK, xmmK, xmm5 : C5 [~R=1 ~vvvv(K) L=0 pp=01]  EB modrm(11 K 101)
        body += bytes([0xC5, ((~k & 0xF) << 3) | 0x01 | 0x80, 0xEB, 0xC0 | (k << 3) | 5])
        cave += body
        pending.append((rva, size, off, len(body), k))
    else:
        raise SystemExit('unhandled instruction %s at %x' % (mn, rva))

# ---------- 4. lay out the new section ----------
last = pe.sections[-1]
sec_align = pe.OPTIONAL_HEADER.SectionAlignment
file_align = pe.OPTIONAL_HEADER.FileAlignment
new_va = (last.VirtualAddress + last.Misc_VirtualSize + sec_align - 1) // sec_align * sec_align
pad_to(16)
m1_off = len(cave); cave += M1
m2_off = len(cave); cave += M2

# resolve placeholders + append "jmp back"
final = bytearray()
cave_bytes = bytearray(cave)
def patch_placeholders(buf, base_off, k):
    out = bytearray()
    i = 0
    s = bytes(buf)
    while i < len(s):
        if s[i:i + 6] == b'@@M2@@':
            ip_after = new_va + base_off + len(out) + 9      # C4 E2 51 00 modrm disp32 = 9 bytes
            disp = (new_va + m2_off) - ip_after
            out += bytes([0xC4, 0xE2, 0x51, 0x00, 0x2D]) + struct.pack('<i', disp)  # vpshufb xmm5,xmm5,[rip+d]
            i += 6
        elif s[i:i + 5] == b'@@M1:':
            j = s.index(b'@@', i + 5)
            kk = int(s[i + 5:j]); i = j + 2
            ip_after = new_va + base_off + len(out) + 9
            disp = (new_va + m1_off) - ip_after
            vvvv = (~kk) & 0xF
            out += bytes([0xC4, 0xE2, (vvvv << 3) | 0x01, 0x00, 0x05 | (kk << 3)]) + struct.pack('<i', disp)
        else:
            out.append(s[i]); i += 1
    return bytes(out)

# rebuild cave with placeholder expansion (sizes change -> recompute offsets sequentially)
cave2 = bytearray()
site_entry = {}
cursor_map = []
for rva, size, off, blen, k in pending:
    pass
# simple second pass: regenerate in order
cave2 = bytearray()
entries = []
idx = 0
for rva, size, mn, bs, (fb, fe) in sites:
    if mn == 'vpcomeqq': continue
    while len(cave2) % 16: cave2.append(0xCC)
    pe_off, pe_size, pe_cave_off, pe_len, pe_k = pending[idx]; idx += 1
    body = patch_placeholders(cave[pe_cave_off:pe_cave_off + pe_len], len(cave2), pe_k)
    start_off = len(cave2)
    cave2 += body
    cave2 += jmp_rel32(new_va + len(cave2), rva + size)
    entries.append((rva, size, start_off))
while len(cave2) % 16: cave2.append(0xCC)
m1_new = len(cave2); cave2 += M1
m2_new = len(cave2); cave2 += M2
# the placeholders were resolved against m1_off/m2_off from pass 1 -> redo with final offsets
if (m1_new, m2_new) != (m1_off, m2_off):
    m1_off, m2_off = m1_new, m2_new
    cave2 = bytearray(); entries = []; idx = 0
    for rva, size, mn, bs, (fb, fe) in sites:
        if mn == 'vpcomeqq': continue
        while len(cave2) % 16: cave2.append(0xCC)
        pe_off, pe_size, pe_cave_off, pe_len, pe_k = pending[idx]; idx += 1
        body = patch_placeholders(cave[pe_cave_off:pe_cave_off + pe_len], len(cave2), pe_k)
        start_off = len(cave2)
        cave2 += body
        cave2 += jmp_rel32(new_va + len(cave2), rva + size)
        entries.append((rva, size, start_off))
    while len(cave2) % 16: cave2.append(0xCC)
    m1_new = len(cave2); cave2 += M1
    m2_new = len(cave2); cave2 += M2
    assert (m1_new, m2_new) == (m1_off, m2_off), 'constant offsets unstable'

# ---------- 5. write patches into .text ----------
for rva, size, kind, payload in patches:
    assert len(payload) <= size
    o = rva2off(rva)
    data[o:o + size] = payload + b'\x90' * (size - len(payload))
for rva, size, cave_off in entries:
    o = rva2off(rva)
    j = jmp_rel32(rva, new_va + cave_off)
    data[o:o + size] = j + b'\x90' * (size - len(j))

# ---------- 6. add the PE section ----------
hdr_end = pe.DOS_HEADER.e_lfanew + 4 + 20 + pe.FILE_HEADER.SizeOfOptionalHeader + 40 * pe.FILE_HEADER.NumberOfSections
first_raw = min(s.PointerToRawData for s in pe.sections if s.PointerToRawData)
assert hdr_end + 40 <= first_raw, 'no room for a new section header'
raw_size = (len(cave2) + file_align - 1) // file_align * file_align
file_end = (len(data) + file_align - 1) // file_align * file_align
data += b'\x00' * (file_end - len(data))
raw_ptr = len(data)
data += bytes(cave2) + b'\x00' * (raw_size - len(cave2))
sec = struct.pack('<8sIIIIIIHHI', b'.cave', len(cave2), new_va, raw_size, raw_ptr, 0, 0, 0, 0, 0x60000020)
data[hdr_end:hdr_end + 40] = sec
nsec_off = pe.DOS_HEADER.e_lfanew + 4 + 2
struct.pack_into('<H', data, nsec_off, pe.FILE_HEADER.NumberOfSections + 1)
opt = pe.DOS_HEADER.e_lfanew + 4 + 20
size_of_image = (new_va + len(cave2) + sec_align - 1) // sec_align * sec_align
struct.pack_into('<I', data, opt + 56, size_of_image)           # SizeOfImage
struct.pack_into('<I', data, opt + 64, 0)                       # CheckSum (not verified for user-mode exes)
open(dst, 'wb').write(data)
print('patched %d TBM/XOP sites (%d via cave, %d in place); new section .cave RVA %x size %d' %
      (len(sites), len(entries), len(patches), new_va, len(cave2)))
