#define _GNU_SOURCE
#include <assert.h>
#include <fcntl.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

typedef int (*Worker)(void *, uint64_t *, int, int);
#if defined(__x86_64__)
typedef int (__attribute__((ms_abi)) *MsWorker)(void *, uint64_t *, int, int);
#endif
typedef struct { void *ptr; size_t size; bool win; } Code;
typedef struct { uint32_t word, link; } Row;

static Code load(const char *abi, const char *name)
{
    char path[100];
    snprintf(path, sizeof(path), ".temp/native-word32/%s-%s.bin", abi, name);
    int fd = open(path, O_RDONLY);
    assert(fd >= 0);
    struct stat file;
    assert(fstat(fd, &file) == 0);
    void *ptr = mmap(NULL, (size_t)file.st_size, PROT_READ | PROT_EXEC, MAP_PRIVATE, fd, 0);
    assert(ptr != MAP_FAILED && close(fd) == 0);
    return (Code){ptr, (size_t)file.st_size, !strcmp(abi, "ms")};
}

static int enter(Code code, void *rows, uint64_t *slots, int resume, int fuel)
{
#if defined(__x86_64__)
    if (code.win) return ((MsWorker)code.ptr)(rows, slots, resume, fuel);
#endif
    return ((Worker)code.ptr)(rows, slots, resume, fuel);
}

enum Op { AND, OR, XOR, NOT, SHL, SHR, SAR, ROL, ROR, POP, CLZ, CTZ, REV };
static const char *names[] = {
    "And", "Or", "Xor", "Not", "ShiftLeft", "ShiftRight", "ShiftRightSigned",
    "RotateLeft", "RotateRight", "PopCount", "LeadingZeroCount", "TrailingZeroCount", "ReverseBytes",
};

static uint32_t reference(enum Op op, uint32_t x, uint32_t y)
{
    switch (op) {
    case AND: return x & y;
    case OR: return x | y;
    case XOR: return x ^ y;
    case NOT: return ~x;
    case SHL: return x << y;
    case SHR: return x >> y;
    case SAR: return (x >> y) | ((x >> 31) && y ? UINT32_MAX << (32 - y) : 0);
    case ROL: return y ? (x << y) | (x >> (32 - y)) : x;
    case ROR: return y ? (x >> y) | (x << (32 - y)) : x;
    case POP: {
        uint32_t n = 0;
        for (unsigned i = 0; i < 32; i++) n += (x >> i) & 1;
        return n;
    }
    case CLZ:
        for (unsigned i = 0; i < 32; i++) if (x & (UINT32_C(1) << (31 - i))) return i;
        return 32;
    case CTZ:
        for (unsigned i = 0; i < 32; i++) if (x & (UINT32_C(1) << i)) return i;
        return 32;
    case REV:
        return (x << 24) | ((x & UINT32_C(0xff00)) << 8) | ((x >> 8) & UINT32_C(0xff00)) | (x >> 24);
    }
    assert(!"bad operation");
    return 0;
}

static unsigned cases;

static void check(Code code, enum Op op, uint32_t x, uint32_t y)
{
    uint64_t slots[] = {x, y, UINT64_MAX, UINT64_MAX};
    assert(enter(code, NULL, slots, 0, 1) == -1);
    uint32_t expected = reference(op, x, y);
    if (slots[3] != expected) {
        fprintf(stderr, "%s %08x %08x -> %llx, expected %08x\n",
                names[op], x, y, (unsigned long long)slots[3], expected);
        assert(!"wrong Word32 result");
    }
    cases++;
}

static const uint32_t edges[] = {0, 1, 2, 31, UINT32_C(0x7fffffff), UINT32_C(0x80000000),
    UINT32_C(0xffffffff), UINT32_C(0x00ff11aa), UINT32_C(0xaaaaaaaa)};
#define EDGES (sizeof(edges) / sizeof(*edges))

static void operations(const char *abi)
{
    for (enum Op op = AND; op <= REV; op++) {
        Code code = load(abi, names[op]);
        bool count = op >= SHL && op <= ROR;
        for (unsigned i = 0; i < EDGES; i++)
            for (unsigned j = 0; j < (count ? 32 : EDGES); j++)
                check(code, op, edges[i], count ? j : edges[j]);
        uint32_t x = 71, y = 13;
        for (unsigned i = 0; i < 512; i++) {
            x = x * UINT32_C(1664525) + UINT32_C(1013904223);
            y ^= y << 13; y ^= y >> 17; y ^= y << 5;
            check(code, op, x, count ? y & 31 : y);
        }
        assert(munmap(code.ptr, code.size) == 0);
    }
}

static void conversion(const char *abi)
{
    Code code = load(abi, "unsigned");
    for (unsigned i = 0; i < EDGES; i++) {
        uint64_t slots[] = {edges[i], UINT64_MAX, UINT64_MAX};
        assert(enter(code, NULL, slots, 0, 1) == -1);
        double result;
        memcpy(&result, &slots[2], sizeof(result));
        assert(result == (double)edges[i]);
    }
    assert(munmap(code.ptr, code.size) == 0);
}

static uint32_t row_word(uint32_t i) { return i * UINT32_C(0xadface31); }

static void walk(const char *abi)
{
    Code code = load(abi, "walk");
    Row rows[127] = {{0}};
    for (uint32_t i = 1; i < 127; i++) rows[i] = (Row){row_word(i), i - 1};
    uint64_t slots[6 + 9] = {126};
    int resume = 0;
    for (unsigned i = 0; i < 41; i++) {
        resume = enter(code, rows, slots, resume, 3);
        assert(resume == 1 && slots[0] == 126 - 3 * (i + 1));
    }
    assert(enter(code, rows, slots, resume, 3) == -1);
    assert(slots[5] == UINT64_C(0xdeadbeef));
    assert(slots[6] == 1 && slots[13] == 1 && slots[14] == 1);
    for (unsigned i = 7; i <= 12; i++) assert(slots[i] == 126);
    for (uint32_t i = 1; i < 127; i++)
        assert(rows[i].word == reference(ROL, row_word(i), 7) && rows[i].link == i - 1);
    assert(rows[0].word == 0 && rows[0].link == 0);
    assert(munmap(code.ptr, code.size) == 0);
}

static void run(const char *abi)
{
    cases = 0;
    operations(abi);
    conversion(abi);
    walk(abi);
    printf("%s: %u exact Word32 results, 9 unsigned conversions, 126 row writes across 41 resumes\n", abi, cases);
}

int main(void)
{
#if defined(__x86_64__)
    run("sysv");
    run("ms");
#else
    run("arm");
#endif
}
