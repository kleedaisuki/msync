#define _GNU_SOURCE
#include <errno.h>
#include <ctype.h>
#include <fcntl.h>
#include <liburing.h>
#include <pthread.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/sysmacros.h>
#include <sys/uio.h>
#include <sys/vfs.h>
#include <time.h>
#include <unistd.h>

enum { CHUNK = 65536, VECTOR_COUNT = 4, SAMPLES = 7 };
struct sample { double elapsed_ms; double cpu_ms; double setup_ms; };
typedef void (*copy_fn)(int, int, char *, double *);
static int matrix_main(int argc, char **argv);

static void die(const char *message) { perror(message); exit(EXIT_FAILURE); }
static void ring_error(int rc, const char *message) { if (rc < 0) { errno = -rc; die(message); } }

static double wall_ms(void) {
    struct timespec now;
    if (clock_gettime(CLOCK_MONOTONIC, &now)) die("clock_gettime");
    return now.tv_sec * 1000.0 + now.tv_nsec / 1000000.0;
}

static double cpu_ms(void) {
    struct rusage use;
    if (getrusage(RUSAGE_SELF, &use)) die("getrusage");
    return (use.ru_utime.tv_sec + use.ru_stime.tv_sec) * 1000.0 +
           (use.ru_utime.tv_usec + use.ru_stime.tv_usec) / 1000.0;
}

static ssize_t checked_read(int fd, char *buffer, size_t bytes) {
    ssize_t count;
    do { count = read(fd, buffer, bytes); } while (count < 0 && errno == EINTR);
    if (count < 0) die("read");
    return count;
}

static void write_all(int fd, const char *buffer, size_t bytes) {
    while (bytes) {
        ssize_t written = write(fd, buffer, bytes);
        if (written < 0 && errno == EINTR) continue;
        if (written <= 0) die("write");
        buffer += written;
        bytes -= (size_t)written;
    }
}

static int vectors(struct iovec *iov, char *buffer, size_t bytes) {
    int count = 0;
    while (bytes) {
        size_t length = bytes < CHUNK / VECTOR_COUNT ? bytes : CHUNK / VECTOR_COUNT;
        iov[count++] = (struct iovec){ .iov_base = buffer, .iov_len = length };
        buffer += length;
        bytes -= length;
    }
    return count;
}

static void buffered(int input, int output, char *buffer, double *setup) {
    (void)setup;
    ssize_t count;
    while ((count = checked_read(input, buffer, CHUNK))) write_all(output, buffer, (size_t)count);
}

static void vectored(int input, int output, char *buffer, double *setup) {
    (void)setup;
    for (;;) {
        struct iovec iov[VECTOR_COUNT];
        int count = vectors(iov, buffer, CHUNK);
        ssize_t received;
        do { received = readv(input, iov, count); } while (received < 0 && errno == EINTR);
        if (received < 0) die("readv");
        if (!received) return;
        size_t offset = 0;
        while (offset < (size_t)received) {
            count = vectors(iov, buffer + offset, (size_t)received - offset);
            ssize_t written = writev(output, iov, count);
            if (written < 0 && errno == EINTR) continue;
            if (written <= 0) die("writev");
            offset += (size_t)written;
        }
    }
}

static void range_copy(int input, int output, char *buffer, double *setup) {
    (void)setup;
    for (;;) {
        ssize_t copied = copy_file_range(input, NULL, output, NULL, 1024 * 1024, 0);
        if (copied > 0) continue;
        if (!copied) return;
        if (errno == EINTR) continue;
        if (errno == EXDEV || errno == EOPNOTSUPP || errno == EINVAL || errno == ENOSYS) {
            buffered(input, output, buffer, setup);
            return;
        }
        die("copy_file_range");
    }
}

static int complete(struct io_uring *ring) {
    int rc;
    do { rc = io_uring_submit_and_wait(ring, 1); } while (rc == -EINTR);
    ring_error(rc, "io_uring_submit_and_wait");
    struct io_uring_cqe *cqe;
    do { rc = io_uring_wait_cqe(ring, &cqe); } while (rc == -EINTR);
    ring_error(rc, "io_uring_wait_cqe");
    int result = cqe->res;
    io_uring_cqe_seen(ring, cqe);
    ring_error(result, "io_uring completion");
    return result;
}

static void uring_copy(int input, int output, char *buffer, double *setup) {
    struct io_uring ring;
    double started = wall_ms();
    ring_error(io_uring_queue_init(8, &ring, 0), "io_uring_queue_init");
    *setup = wall_ms() - started;
    off_t offset = 0;
    for (;;) {
        struct iovec iov[VECTOR_COUNT];
        int count = vectors(iov, buffer, CHUNK);
        struct io_uring_sqe *sqe = io_uring_get_sqe(&ring);
        if (!sqe) { errno = ENOBUFS; die("io_uring_get_sqe"); }
        io_uring_prep_readv(sqe, input, iov, (unsigned)count, (uint64_t)offset);
        int received = complete(&ring);
        if (!received) break;
        int written = 0;
        while (written < received) {
            count = vectors(iov, buffer + written, (size_t)(received - written));
            sqe = io_uring_get_sqe(&ring);
            if (!sqe) { errno = ENOBUFS; die("io_uring_get_sqe"); }
            io_uring_prep_writev(sqe, output, iov, (unsigned)count, (uint64_t)(offset + written));
            int completed = complete(&ring);
            if (!completed) { errno = EIO; die("zero io_uring write"); }
            written += completed;
        }
        offset += received;
    }
    io_uring_queue_exit(&ring);
}

static void verify(int input, int output) {
    char left[CHUNK], right[CHUNK];
    if (lseek(input, 0, SEEK_SET) < 0 || lseek(output, 0, SEEK_SET) < 0) die("lseek verify");
    for (;;) {
        ssize_t count = checked_read(input, left, sizeof(left));
        if (checked_read(output, right, (size_t)count) != count || memcmp(left, right, (size_t)count)) {
            errno = EIO; die("content mismatch");
        }
        if (!count) break;
    }
    if (checked_read(output, right, 1)) { errno = EIO; die("trailing output"); }
}

static int compare(const void *left, const void *right) {
    double a = *(const double *)left, b = *(const double *)right;
    return (a > b) - (a < b);
}

static void measure(const char *name, copy_fn function, int input, int output, char *buffer) {
    struct sample samples[SAMPLES];
    double elapsed[SAMPLES], cpu[SAMPLES], setup[SAMPLES];
    for (int iteration = -1; iteration < SAMPLES; ++iteration) {
        if (lseek(input, 0, SEEK_SET) < 0 || lseek(output, 0, SEEK_SET) < 0 || ftruncate(output, 0)) die("reset files");
        struct sample value = { .elapsed_ms = wall_ms(), .cpu_ms = cpu_ms(), .setup_ms = 0 };
        function(input, output, buffer, &value.setup_ms);
        value.cpu_ms = cpu_ms() - value.cpu_ms;
        value.elapsed_ms = wall_ms() - value.elapsed_ms;
        verify(input, output);
        if (iteration >= 0) samples[iteration] = value;
    }
    for (int index = 0; index < SAMPLES; ++index) {
        elapsed[index] = samples[index].elapsed_ms;
        cpu[index] = samples[index].cpu_ms;
        setup[index] = samples[index].setup_ms;
    }
    qsort(elapsed, SAMPLES, sizeof(double), compare);
    qsort(cpu, SAMPLES, sizeof(double), compare);
    qsort(setup, SAMPLES, sizeof(double), compare);
    printf("%s elapsed_ms=%.3f cpu_ms=%.3f ring_setup_ms=%.3f samples=%d\n", name,
           elapsed[SAMPLES / 2], cpu[SAMPLES / 2], setup[SAMPLES / 2], SAMPLES);
}

int main(int argc, char **argv) {
    setvbuf(stdout, NULL, _IOLBF, 0);
    if (argc > 1 && !strcmp(argv[1], "--matrix")) return matrix_main(argc, argv);
    if (argc != 3) { fprintf(stderr, "usage: %s WORK_DIRECTORY SIZE_MIB\n", argv[0]); return 2; }
    if (chdir(argv[1])) die("chdir");
    char *end;
    unsigned long mib = strtoul(argv[2], &end, 10);
    if (*end || !mib || mib > 1024) { fprintf(stderr, "SIZE_MIB must be 1..1024\n"); return 2; }
    size_t bytes = mib * 1024 * 1024;
    char *buffer = malloc(CHUNK);
    if (!buffer) die("malloc");
    for (size_t index = 0; index < CHUNK; ++index) buffer[index] = (char)(index % 251);
    int input = open("source.bin", O_CREAT | O_TRUNC | O_RDWR | O_CLOEXEC, 0600);
    int output = open("target.bin", O_CREAT | O_TRUNC | O_RDWR | O_CLOEXEC, 0600);
    if (input < 0 || output < 0) die("open fixture");
    for (size_t offset = 0; offset < bytes; offset += CHUNK) write_all(input, buffer, CHUNK);
    printf("fixture_bytes=%zu chunk=%d vectors=%d warm_cache=1 fsync=0\n", bytes, CHUNK, VECTOR_COUNT);
    struct io_uring ring;
    struct io_uring_params params = {0};
    int supported = io_uring_queue_init_params(8, &ring, &params);
    if (!supported) {
        struct io_uring_probe *probe = io_uring_get_probe_ring(&ring);
        printf("io_uring features=0x%x readv=%d writev=%d splice=%d send_zc=%d\n", params.features,
               probe ? io_uring_opcode_supported(probe, IORING_OP_READV) : -1,
               probe ? io_uring_opcode_supported(probe, IORING_OP_WRITEV) : -1,
               probe ? io_uring_opcode_supported(probe, IORING_OP_SPLICE) : -1,
               probe ? io_uring_opcode_supported(probe, IORING_OP_SEND_ZC) : -1);
        io_uring_free_probe(probe);
        io_uring_queue_exit(&ring);
    } else printf("io_uring unavailable: %s\n", strerror(-supported));
    measure("read/write", buffered, input, output, buffer);
    measure("readv/writev", vectored, input, output, buffer);
    measure("copy_file_range", range_copy, input, output, buffer);
    if (!supported) measure("io_uring readv/writev depth1", uring_copy, input, output, buffer);
    if (close(input) || close(output)) die("close");
    free(buffer);
    return 0;
}

enum matrix_backend { MATRIX_BUFFERED, MATRIX_VECTOR, MATRIX_RANGE, MATRIX_RING, MATRIX_FIXED };
struct matrix_flow {
    int input, output;
    off_t base, length, next;
    size_t capacity;
    enum matrix_backend backend;
    int failure;
    bool used_fallback;
};

static int matrix_vectors(struct iovec *iov, char *buffer, size_t bytes) {
    int count = 0;
    size_t width = (bytes + VECTOR_COUNT - 1) / VECTOR_COUNT;
    while (bytes) {
        size_t length = bytes < width ? bytes : width;
        iov[count++] = (struct iovec){ .iov_base = buffer, .iov_len = length };
        buffer += length;
        bytes -= length;
    }
    return count;
}

static int matrix_copy_buffered(struct matrix_flow *flow, off_t completed) {
    char *buffer = malloc(flow->capacity);
    if (!buffer) return ENOMEM;
    memset(buffer, 0, flow->capacity);
    while (completed < flow->length) {
        size_t wanted = (size_t)(flow->length - completed);
        if (wanted > flow->capacity) wanted = flow->capacity;
        struct iovec iov[VECTOR_COUNT];
        int count = matrix_vectors(iov, buffer, wanted);
        ssize_t received = flow->backend == MATRIX_VECTOR
            ? preadv(flow->input, iov, count, flow->base + completed)
            : pread(flow->input, buffer, wanted, flow->base + completed);
        if (received < 0 && errno == EINTR) continue;
        if (received <= 0) { int error = received ? errno : EIO; free(buffer); return error; }
        ssize_t written = 0;
        while (written < received) {
            count = matrix_vectors(iov, buffer + written, (size_t)(received - written));
            ssize_t result = flow->backend == MATRIX_VECTOR
                ? pwritev(flow->output, iov, count, completed + written)
                : pwrite(flow->output, buffer + written, (size_t)(received - written), completed + written);
            if (result < 0 && errno == EINTR) continue;
            if (result <= 0) { int error = result ? errno : EIO; free(buffer); return error; }
            written += result;
        }
        completed += received;
    }
    free(buffer);
    return 0;
}

static void *matrix_worker(void *pointer) {
    struct matrix_flow *flow = pointer;
    off_t completed = 0;
    if (flow->backend == MATRIX_RANGE) {
        while (completed < flow->length) {
            off_t input = flow->base + completed, output = completed;
            size_t wanted = (size_t)(flow->length - completed);
            if (wanted > 1024 * 1024) wanted = 1024 * 1024;
            ssize_t count = copy_file_range(flow->input, &input, flow->output, &output, wanted, 0);
            if (count > 0) { completed += count; continue; }
            if (count < 0 && errno == EINTR) continue;
            if (!count || errno == EINVAL || errno == ENOSYS || errno == EOPNOTSUPP || errno == EXDEV || errno == EPERM) {
                flow->used_fallback = true;
                break;
            }
            flow->failure = errno;
            return NULL;
        }
    }
    if (completed < flow->length) flow->failure = matrix_copy_buffered(flow, completed);
    return NULL;
}

static int matrix_range_threads(struct matrix_flow *flows, int count) {
    pthread_t threads[4];
    int started = 0, failure = 0;
    for (; started < count; ++started) {
        int error = pthread_create(&threads[started], NULL, matrix_worker, &flows[started]);
        if (error) { failure = error; break; }
    }
    for (int index = 0; index < started; ++index) {
        int error = pthread_join(threads[index], NULL);
        if (!failure && error) failure = error;
        if (!failure && flows[index].failure) failure = flows[index].failure;
    }
    return failure;
}

struct matrix_work {
    struct matrix_flow *flows;
    pthread_mutex_t mutex;
    int count, cursor, failure;
};

static int matrix_chunk(struct matrix_flow *flow, off_t offset, size_t wanted, char *buffer) {
    size_t received = 0;
    while (received < wanted) {
        struct iovec iov[VECTOR_COUNT];
        int vectors = matrix_vectors(iov, buffer + received, wanted - received);
        ssize_t count = flow->backend == MATRIX_VECTOR
            ? preadv(flow->input, iov, vectors, flow->base + offset + (off_t)received)
            : pread(flow->input, buffer + received, wanted - received, flow->base + offset + (off_t)received);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) return count ? errno : EIO;
        received += (size_t)count;
    }
    size_t written = 0;
    while (written < received) {
        struct iovec iov[VECTOR_COUNT];
        int vectors = matrix_vectors(iov, buffer + written, received - written);
        ssize_t count = flow->backend == MATRIX_VECTOR
            ? pwritev(flow->output, iov, vectors, offset + (off_t)written)
            : pwrite(flow->output, buffer + written, received - written, offset + (off_t)written);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) return count ? errno : EIO;
        written += (size_t)count;
    }
    return 0;
}

static void *matrix_parallel_worker(void *pointer) {
    struct matrix_work *work = pointer;
    char *buffer = malloc(CHUNK);
    if (buffer) memset(buffer, 0, CHUNK);
    for (;;) {
        pthread_mutex_lock(&work->mutex);
        if (!buffer) work->failure = ENOMEM;
        struct matrix_flow *flow = NULL;
        for (int attempt = 0; !work->failure && attempt < work->count; ++attempt) {
            struct matrix_flow *candidate = &work->flows[work->cursor++ % work->count];
            if (candidate->next == candidate->length) continue;
            flow = candidate;
            break;
        }
        off_t offset = flow ? flow->next : 0;
        size_t wanted = flow ? (size_t)(flow->length - offset) : 0;
        if (wanted > CHUNK) wanted = CHUNK;
        if (flow) flow->next += (off_t)wanted;
        pthread_mutex_unlock(&work->mutex);
        if (!flow) break;
        int failure = matrix_chunk(flow, offset, wanted, buffer);
        if (!failure) continue;
        pthread_mutex_lock(&work->mutex);
        if (!work->failure) work->failure = failure;
        pthread_mutex_unlock(&work->mutex);
        break;
    }
    free(buffer);
    return NULL;
}

static int matrix_threads(struct matrix_flow *flows, int count, unsigned depth) {
    if (flows[0].backend == MATRIX_RANGE) return matrix_range_threads(flows, count);
    struct matrix_work work = { .flows = flows, .mutex = PTHREAD_MUTEX_INITIALIZER, .count = count };
    pthread_t threads[64];
    unsigned started = 0;
    for (; started < depth; ++started) {
        int failure = pthread_create(&threads[started], NULL, matrix_parallel_worker, &work);
        if (!failure) continue;
        pthread_mutex_lock(&work.mutex);
        work.failure = failure;
        pthread_mutex_unlock(&work.mutex);
        break;
    }
    for (unsigned index = 0; index < started; ++index) {
        int failure = pthread_join(threads[index], NULL);
        if (failure) { errno = failure; die("join matrix worker"); }
    }
    pthread_mutex_destroy(&work.mutex);
    return work.failure;
}

struct matrix_slot {
    struct matrix_flow *flow;
    char *data;
    struct iovec iov[VECTOR_COUNT];
    off_t offset;
    size_t wanted, read, written;
    unsigned index;
    bool writing, pending;
};

static void matrix_submit_slot(struct io_uring *ring, struct matrix_slot *slot, bool fixed) {
    struct io_uring_sqe *sqe = io_uring_get_sqe(ring);
    if (!sqe) { errno = ENOBUFS; die("matrix io_uring_get_sqe"); }
    size_t completed = slot->writing ? slot->written : slot->read;
    off_t offset = slot->offset + (off_t)completed;
    char *data = slot->data + completed;
    unsigned bytes = (unsigned)(slot->wanted - completed);
    if (fixed && slot->writing) io_uring_prep_write_fixed(sqe, slot->flow->output, data, bytes, (uint64_t)offset, (int)slot->index);
    else if (fixed) io_uring_prep_read_fixed(sqe, slot->flow->input, data, bytes, (uint64_t)(slot->flow->base + offset), (int)slot->index);
    else {
        int vectors = matrix_vectors(slot->iov, data, bytes);
        if (slot->writing) io_uring_prep_writev(sqe, slot->flow->output, slot->iov, (unsigned)vectors, (uint64_t)offset);
        else io_uring_prep_readv(sqe, slot->flow->input, slot->iov, (unsigned)vectors, (uint64_t)(slot->flow->base + offset));
    }
    io_uring_sqe_set_data64(sqe, slot->index + 1);
    slot->pending = true;
}

static bool matrix_assign(struct matrix_slot *slot, struct matrix_flow *flows, int count, int *cursor) {
    for (int attempt = 0; attempt < count; ++attempt) {
        struct matrix_flow *flow = &flows[(*cursor)++ % count];
        if (flow->next == flow->length) continue;
        slot->flow = flow;
        slot->offset = flow->next;
        off_t remaining = flow->length - flow->next;
        slot->wanted = remaining < CHUNK ? (size_t)remaining : CHUNK;
        flow->next += (off_t)slot->wanted;
        slot->read = slot->written = 0;
        slot->writing = false;
        return true;
    }
    return false;
}

static int matrix_ring_completion(struct io_uring *ring, struct io_uring_cqe **cqe) {
    int error;
    do { error = io_uring_wait_cqe(ring, cqe); } while (error == -EINTR);
    return error;
}

static int matrix_submit_all(struct io_uring *ring) {
    while (io_uring_sq_ready(ring)) {
        int result = io_uring_submit(ring);
        if (result == -EINTR) continue;
        if (result < 0) return result;
        if (!result) return -EIO;
    }
    return 0;
}

static void matrix_cancel_drain(struct io_uring *ring, unsigned pending) {
    // Pending includes prepared SQEs: submit them before waiting for their CQEs.
    ring_error(matrix_submit_all(ring), "submit pending operations before cancel");
    struct io_uring_sqe *sqe = io_uring_get_sqe(ring);
    if (!sqe) { errno = ENOBUFS; die("cancel queue"); }
    io_uring_prep_cancel(sqe, NULL, IORING_ASYNC_CANCEL_ANY | IORING_ASYNC_CANCEL_ALL);
    io_uring_sqe_set_data64(sqe, UINT64_MAX);
    ring_error(matrix_submit_all(ring), "submit cancel");
    bool cancellation = true;
    while (pending || cancellation) {
        struct io_uring_cqe *cqe;
        ring_error(matrix_ring_completion(ring, &cqe), "drain cancelled operations");
        if (io_uring_cqe_get_data64(cqe) == UINT64_MAX) cancellation = false;
        else --pending;
        io_uring_cqe_seen(ring, cqe);
    }
}

static int matrix_ring(struct matrix_flow *flows, int count, unsigned depth, bool fixed, double *setup) {
    struct io_uring ring;
    double begin = wall_ms();
    int failure = io_uring_queue_init(depth * 2 + 8, &ring, 0);
    if (failure < 0) return -failure;
    char *data = NULL;
    struct matrix_slot *slots = calloc(depth, sizeof(*slots));
    struct iovec *registered = calloc(depth, sizeof(*registered));
    int allocation = posix_memalign((void **)&data, 4096, depth * CHUNK);
    if (!slots || !registered || allocation) { failure = ENOMEM; goto cleanup; }
    memset(data, 0, depth * CHUNK);
    for (unsigned index = 0; index < depth; ++index) {
        slots[index].index = index;
        slots[index].data = data + index * CHUNK;
        registered[index] = (struct iovec){ .iov_base = slots[index].data, .iov_len = CHUNK };
    }
    if (fixed) {
        failure = io_uring_register_buffers(&ring, registered, depth);
        if (failure < 0) { failure = -failure; goto cleanup; }
    }
    *setup = wall_ms() - begin;
    int cursor = 0;
    unsigned pending = 0;
    for (unsigned index = 0; index < depth; ++index) {
        if (!matrix_assign(&slots[index], flows, count, &cursor)) break;
        matrix_submit_slot(&ring, &slots[index], fixed);
        ++pending;
    }
    failure = 0;
    while (pending) {
        int result;
        result = matrix_submit_all(&ring);
        if (result < 0) { failure = -result; matrix_cancel_drain(&ring, pending); break; }
        struct io_uring_cqe *cqe;
        result = matrix_ring_completion(&ring, &cqe);
        if (result < 0) { failure = -result; matrix_cancel_drain(&ring, pending); break; }
        do {
            uint64_t identifier = io_uring_cqe_get_data64(cqe);
            if (!identifier || identifier > depth) { errno = EPROTO; die("invalid completion identifier"); }
            struct matrix_slot *slot = &slots[identifier - 1];
            int completed = cqe->res;
            io_uring_cqe_seen(&ring, cqe);
            slot->pending = false;
            --pending;
            if (completed <= 0) {
                failure = completed ? -completed : EIO;
                matrix_cancel_drain(&ring, pending);
                goto cleanup;
            }
            if (slot->writing) slot->written += (size_t)completed;
            else slot->read += (size_t)completed;
            if (!slot->writing && slot->read == slot->wanted) slot->writing = true;
            bool again = slot->written != slot->wanted || matrix_assign(slot, flows, count, &cursor);
            if (again) { matrix_submit_slot(&ring, slot, fixed); ++pending; }
        } while (!io_uring_peek_cqe(&ring, &cqe));
    }
cleanup:
    io_uring_queue_exit(&ring);
    free(registered);
    free(slots);
    free(data);
    return failure;
}

static long resident_pages(int fd, size_t length) {
    long page = sysconf(_SC_PAGESIZE);
    size_t pages = (length + (size_t)page - 1) / (size_t)page;
    unsigned char *state = calloc(pages, 1);
    if (!state) die("allocate residency vector");
    void *mapping = mmap(NULL, length, PROT_NONE, MAP_SHARED, fd, 0);
    if (mapping == MAP_FAILED) die("mmap residency");
    if (mincore(mapping, length, state)) die("mincore");
    long resident = 0;
    for (size_t index = 0; index < pages; ++index) resident += state[index] & 1;
    if (munmap(mapping, length)) die("munmap residency");
    free(state);
    return resident;
}

static void matrix_pattern(char *buffer, size_t offset) {
    uint64_t block = offset / CHUNK, inverse = ~block;
    for (size_t index = 0; index < CHUNK; ++index) buffer[index] = (char)((index + block * 29) % 251);
    memcpy(buffer, &block, sizeof(block));
    memcpy(buffer + sizeof(block), &inverse, sizeof(inverse));
}

static void matrix_source(int input, size_t length) {
    struct stat info;
    if (fstat(input, &info)) die("fstat source");
    if ((uint64_t)info.st_size == length) return;
    if (ftruncate(input, 0)) die("truncate source fixture");
    char buffer[CHUNK];
    for (size_t offset = 0; offset < length; offset += CHUNK) {
        matrix_pattern(buffer, offset);
        size_t done = 0;
        while (done < CHUNK) {
            ssize_t written = pwrite(input, buffer + done, CHUNK - done, (off_t)(offset + done));
            if (written < 0 && errno == EINTR) continue;
            if (written <= 0) die("fill source fixture");
            done += (size_t)written;
        }
    }
    if (fsync(input)) die("fsync source fixture");
}

static void matrix_warm(int input, size_t length) {
    char buffer[CHUNK];
    size_t completed = 0;
    while (completed < length) {
        ssize_t count = pread(input, buffer, sizeof(buffer), (off_t)completed);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) die("warm source");
        completed += (size_t)count;
    }
}

static void matrix_verify(struct matrix_flow *flows, int count) {
    char expected[CHUNK], actual[CHUNK];
    for (int index = 0; index < count; ++index) {
        struct matrix_flow *flow = &flows[index];
        struct stat info;
        if (fstat(flow->output, &info)) die("verify size");
        if (info.st_size != flow->length) { errno = EIO; die("verified output length"); }
        for (off_t offset = 0; offset < flow->length; offset += CHUNK) {
            matrix_pattern(expected, (size_t)(flow->base + offset));
            size_t completed = 0;
            while (completed < CHUNK) {
                ssize_t received = pread(flow->output, actual + completed, CHUNK - completed, offset + (off_t)completed);
                if (received < 0 && errno == EINTR) continue;
                if (received <= 0) die("verify read");
                completed += (size_t)received;
            }
            if (memcmp(expected, actual, CHUNK)) { errno = EIO; die("verified output contents"); }
        }
    }
}

static unsigned matrix_integer(const char *value, unsigned minimum, unsigned maximum) {
    char *end;
    unsigned long parsed = strtoul(value, &end, 10);
    if (*end || parsed < minimum || parsed > maximum) {
        fprintf(stderr, "invalid integer %s: expected %u..%u\n", value, minimum, maximum);
        exit(2);
    }
    return (unsigned)parsed;
}

static int matrix_main(int argc, char **argv) {
    if (argc != 10) {
        fprintf(stderr, "usage: %s --matrix WORKDIR FS_LABEL SIZE_MIB CACHE(warm|evict) FLOWS(1|4) DEPTH BACKEND(buffered|vector|range|ring|fixed) SAMPLES\n", argv[0]);
        return 2;
    }
    if (chdir(argv[2])) die("matrix chdir");
    const char *label = argv[3], *cache = argv[5], *backend_name = argv[8];
    for (const char *character = label; *character; ++character) {
        if (!isalnum((unsigned char)*character) && *character != '-' && *character != '_') return 2;
    }
    unsigned mib = matrix_integer(argv[4], 1, 4096);
    unsigned count = matrix_integer(argv[6], 1, 4);
    if (count != 1 && count != 4) { fprintf(stderr, "flow count must be 1 or 4\n"); return 2; }
    unsigned depth = matrix_integer(argv[7], 1, 64);
    unsigned samples = matrix_integer(argv[9], 1, 7);
    bool evict = !strcmp(cache, "evict");
    if (!evict && strcmp(cache, "warm")) return 2;
    enum matrix_backend backend;
    if (!strcmp(backend_name, "buffered")) backend = MATRIX_BUFFERED;
    else if (!strcmp(backend_name, "vector")) backend = MATRIX_VECTOR;
    else if (!strcmp(backend_name, "range")) backend = MATRIX_RANGE;
    else if (!strcmp(backend_name, "ring")) backend = MATRIX_RING;
    else if (!strcmp(backend_name, "fixed")) backend = MATRIX_FIXED;
    else return 2;
    size_t length = (size_t)mib * 1024 * 1024;
    if (length % (count * CHUNK)) return 2;
    int input = open("matrix-source-v2.bin", O_CREAT | O_RDWR | O_CLOEXEC | O_NOFOLLOW, 0600);
    if (input < 0) die("matrix source open");
    matrix_source(input, length);
    struct stat source_info;
    struct statfs source_fs;
    if (fstat(input, &source_info) || fstatfs(input, &source_fs)) die("opened source filesystem");
    struct matrix_flow flows[4] = {0};
    for (unsigned index = 0; index < count; ++index) {
        char name[64];
        if (snprintf(name, sizeof(name), "matrix-output-%u.bin", index) >= (int)sizeof(name)) return 2;
        int output = open(name, O_CREAT | O_RDWR | O_CLOEXEC | O_NOFOLLOW, 0600);
        if (output < 0) die("matrix target open");
        struct stat target_info;
        struct statfs target_fs;
        if (fstat(output, &target_info) || fstatfs(output, &target_fs)) die("opened target filesystem");
        if (source_fs.f_type != target_fs.f_type || source_info.st_dev != target_info.st_dev) {
            fprintf(stderr, "source and target are not on the same filesystem\n"); return 2;
        }
        flows[index] = (struct matrix_flow){ .input = input, .output = output,
            .base = (off_t)(length / count * index), .length = (off_t)(length / count),
            .capacity = (size_t)depth * CHUNK / count, .backend = backend };
    }
    for (unsigned sample = 0; sample < samples; ++sample) {
        if (fsync(input)) die("matrix source sync");
        bool eviction_ok = true;
        for (unsigned index = 0; index < count; ++index) {
            if (fsync(flows[index].output) || ftruncate(flows[index].output, 0)) die("reset matrix output");
            if (posix_fadvise(flows[index].output, 0, 0, POSIX_FADV_DONTNEED)) eviction_ok = false;
            flows[index].next = 0;
            flows[index].failure = 0;
            flows[index].used_fallback = false;
        }
        if (evict) {
            if (posix_fadvise(input, 0, (off_t)length, POSIX_FADV_DONTNEED)) eviction_ok = false;
        } else matrix_warm(input, length);
        long resident = resident_pages(input, length);
        struct rusage usage_before, usage_after;
        if (getrusage(RUSAGE_SELF, &usage_before)) die("usage before");
        double setup = 0, started = wall_ms(), cpu = cpu_ms();
        int error = backend == MATRIX_RING || backend == MATRIX_FIXED
            ? matrix_ring(flows, (int)count, depth, backend == MATRIX_FIXED, &setup)
            : matrix_threads(flows, (int)count, depth);
        cpu = cpu_ms() - cpu;
        double elapsed = wall_ms() - started;
        if (getrusage(RUSAGE_SELF, &usage_after)) die("usage after");
        if (error) {
            fprintf(stderr, "matrix backend %s failed: %s\n", backend_name, strerror(error));
            for (unsigned index = 0; index < count; ++index) close(flows[index].output);
            close(input);
            return 1;
        }
        started = wall_ms();
        bool fallback = false;
        for (unsigned index = 0; index < count; ++index) {
            if (fsync(flows[index].output)) die("matrix output sync");
            fallback |= flows[index].used_fallback;
        }
        double flush = wall_ms() - started;
        matrix_verify(flows, (int)count);
        printf("{\"fs\":\"%s\",\"fstatfs_type\":\"0x%lx\",\"device\":\"%u:%u\",\"bytes\":%zu,\"cache\":\"%s\",\"eviction_api_ok\":%s,\"resident_pages_before\":%ld,\"page_size\":%ld,\"flows\":%u,\"depth\":%u,\"backend\":\"%s\",\"data_capacity_bytes\":%zu,\"sample\":%u,\"copy_ms\":%.3f,\"process_cpu_ms\":%.3f,\"ring_setup_ms\":%.3f,\"flush_ms\":%.3f,\"inblock\":%ld,\"outblock\":%ld,\"range_fallback\":%s,\"verified\":true}\n",
               label, (unsigned long)source_fs.f_type, major(source_info.st_dev), minor(source_info.st_dev), length,
               cache, eviction_ok ? "true" : "false", resident, sysconf(_SC_PAGESIZE), count, depth, backend_name,
               (size_t)depth * CHUNK, sample, elapsed, cpu, setup, flush,
               usage_after.ru_inblock - usage_before.ru_inblock, usage_after.ru_oublock - usage_before.ru_oublock,
               fallback ? "true" : "false");
    }
    for (unsigned index = 0; index < count; ++index) if (close(flows[index].output)) die("matrix target close");
    if (close(input)) die("matrix source close");
    return 0;
}
