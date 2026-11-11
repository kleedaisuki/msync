using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Msync.Core;
using Msync.Runtime;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
string kind = args[0];
int size = int.Parse(args[1]);
int warmups = int.Parse(args[2]);
int samples = int.Parse(args[3]);
int batch = int.Parse(args[4]);
string expected = args[5];
if (kind == "sha256")
{
    uint[] input = File.ReadAllText(args[6]).Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(uint.Parse).ToArray();
    uint[] output = [];
    Measure(() => output = Sha256(input), () => string.Concat(output.Select(word => word.ToString("x8"))));
}
else if (kind == "msync")
{
    Module module = Artifact.Read(args[6]);
    ExecutionOptions options = new(WorkingDirectory: Environment.CurrentDirectory,
        Output: TextWriter.Null, Error: TextWriter.Null);
    Value result = Value.Null;
    Measure(() => result = Interpreter.ExecuteAsync(module, options).GetAwaiter().GetResult(),
        () => result is ScopeValue hash
            ? string.Concat(Enumerable.Range(0, 8).Select(i => ((uint)((NumberValue)hash[$"h{i}"]).Data).ToString("x8")))
            : ((NumberValue)result).Data.ToString("F0"));
}
else
{
    int[] ordered = kind == "heap" ? Enumerable.Range(1, size).Select(Key).Order().ToArray() : [];
    int result = 0;
    Measure(() => result = kind switch
    {
        "tree" => Set(new SortedSet<int>(), size),
        "hash" => Set(new HashSet<int>(), size),
        "heap" => Heap(size, ordered),
        _ => throw new ArgumentException(kind)
    }, () => result.ToString());
}

void Measure(Action run, Func<string> result)
{
    for (int sample = -warmups; sample < samples; sample++)
    {
        long start = Stopwatch.GetTimestamp();
        for (int iteration = 0; iteration < batch; iteration++)
            run();
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds / batch;
        if (result() != expected) throw new InvalidOperationException($"Expected {expected}; got {result()}.");
        if (sample >= 0) Console.WriteLine(milliseconds.ToString("R"));
    }
}

static int Key(int index) => 2 * (index * 40503 % 65536);

static int Set<T>(T values, int size) where T : ISet<int>
{
    int score = 0;
    for (int i = 1; i <= size; i++)
        if (values.Add(Key(i))) score++;
    for (int i = 1; i <= size; i++)
    {
        if (values.Contains(Key(i))) score += 3;
        if (!values.Contains(Key(i) + 1)) score += 5;
    }
    return score;
}

static int Heap(int size, int[] ordered)
{
    PriorityQueue<int, int> queue = new();
    for (int i = 1; i <= size; i++) queue.Enqueue(Key(i), Key(i));
    int score = 0;
    foreach (int expected in ordered)
    {
        if (queue.Peek() == expected) score++;
        queue.Dequeue();
    }
    return score;
}

static uint[] Sha256(uint[] input)
{
    uint[] hash = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
        0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19];
    Span<uint> w = stackalloc uint[64];
    for (int offset = 64; offset < input.Length; offset += 16)
    {
        input.AsSpan(offset, 16).CopyTo(w);
        for (int i = 16; i < 64; i++)
        {
            uint x = w[i - 15], y = w[i - 2];
            uint s0 = BitOperations.RotateRight(x, 7) ^ BitOperations.RotateRight(x, 18) ^ (x >> 3);
            uint s1 = BitOperations.RotateRight(y, 17) ^ BitOperations.RotateRight(y, 19) ^ (y >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        uint a = hash[0], b = hash[1], c = hash[2], d = hash[3];
        uint e = hash[4], f = hash[5], g = hash[6], h = hash[7];
        for (int i = 0; i < 64; i++)
        {
            uint s1 = BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11) ^ BitOperations.RotateRight(e, 25);
            uint t1 = h + s1 + ((e & f) ^ (~e & g)) + input[i] + w[i];
            uint s0 = BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13) ^ BitOperations.RotateRight(a, 22);
            uint t2 = s0 + ((a & b) ^ (a & c) ^ (b & c));
            h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
        }
        hash[0] += a; hash[1] += b; hash[2] += c; hash[3] += d;
        hash[4] += e; hash[5] += f; hash[6] += g; hash[7] += h;
    }
    return hash;
}
