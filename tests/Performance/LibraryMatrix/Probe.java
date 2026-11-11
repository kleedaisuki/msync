import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.HashSet;
import java.util.HexFormat;
import java.util.PriorityQueue;
import java.util.Set;
import java.util.TreeSet;

public class Probe {
    private static volatile int[] sink;

    static int[] sha256(int[] input) {
        int[] hash = {0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,
            0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19};
        int[] w = new int[64];
        for (int base = 64; base < input.length; base += 16) {
            System.arraycopy(input, base, w, 0, 16);
            for (int i = 16; i < 64; ++i) {
                int x=w[i-15], y=w[i-2];
                int s0=Integer.rotateRight(x,7)^Integer.rotateRight(x,18)^(x>>>3);
                int s1=Integer.rotateRight(y,17)^Integer.rotateRight(y,19)^(y>>>10);
                w[i]=w[i-16]+s0+w[i-7]+s1;
            }
            int a=hash[0],b=hash[1],c=hash[2],d=hash[3],e=hash[4],f=hash[5],g=hash[6],h=hash[7];
            for (int i = 0; i < 64; ++i) {
                int s1=Integer.rotateRight(e,6)^Integer.rotateRight(e,11)^Integer.rotateRight(e,25);
                int t1=h+s1+((e&f)^(~e&g))+input[i]+w[i];
                int s0=Integer.rotateRight(a,2)^Integer.rotateRight(a,13)^Integer.rotateRight(a,22);
                int t2=s0+((a&b)^(a&c)^(b&c));
                h=g; g=f; f=e; e=d+t1; d=c; c=b; b=a; a=t1+t2;
            }
            hash[0]+=a; hash[1]+=b; hash[2]+=c; hash[3]+=d;
            hash[4]+=e; hash[5]+=f; hash[6]+=g; hash[7]+=h;
        }
        return hash;
    }

    static int key(int index) { return 2 * ((index * 40503) % 65536); }

    static int set(Set<Integer> values, int size) {
        int score = 0;
        for (int i=1; i<=size; ++i) if (values.add(key(i))) score++;
        for (int i=1; i<=size; ++i) {
            if (values.contains(key(i))) score += 3;
            if (!values.contains(key(i)+1)) score += 5;
        }
        return score;
    }

    static int heap(int size, int[] ordered) {
        PriorityQueue<Integer> values = new PriorityQueue<>();
        for (int i=1; i<=size; ++i) values.add(key(i));
        int score = 0;
        for (int expected : ordered) {
            if (values.element() == expected) score++;
            values.remove();
        }
        return score;
    }

    public static void main(String[] args) throws Exception {
        String kind = args[0], expected = args[5];
        int size = Integer.parseInt(args[1]), warmups = Integer.parseInt(args[2]),
            samples = Integer.parseInt(args[3]), batch = Integer.parseInt(args[4]);
        int[] input = kind.equals("sha256")
            ? Arrays.stream(Files.readString(Path.of(args[6])).strip().split("\\s+"))
                .mapToInt(Integer::parseUnsignedInt).toArray()
            : new int[0];
        int[] ordered = new int[size];
        if (kind.equals("heap")) {
            for (int i=0; i<size; ++i) ordered[i] = key(i+1);
            Arrays.sort(ordered);
        }
        for (int s=-warmups; s<samples; ++s) {
            long start = System.nanoTime();
            int score = 0;
            for (int i=0; i<batch; ++i) {
                switch (kind) {
                    case "tree" -> score = set(new TreeSet<>(), size);
                    case "hash" -> score = set(new HashSet<>(), size);
                    case "heap" -> score = heap(size, ordered);
                    case "sha256" -> sink = sha256(input);
                    default -> throw new IllegalArgumentException(kind);
                }
            }
            double elapsed = (System.nanoTime()-start)/1e6/batch;
            StringBuilder digest = new StringBuilder();
            if (kind.equals("sha256")) for (int word : sink) digest.append(HexFormat.of().toHexDigits(word));
            String result = kind.equals("sha256") ? digest.toString() : Integer.toString(score);
            if (!expected.equals(result)) throw new IllegalStateException(result);
            if (s >= 0) System.out.println(elapsed);
        }
    }
}
