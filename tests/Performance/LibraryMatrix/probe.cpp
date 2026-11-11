#include <algorithm>
#include <array>
#include <bit>
#include <chrono>
#include <cstdint>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <queue>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <unordered_set>
#include <vector>

using Words = std::vector<uint32_t>;

static std::array<uint32_t, 8> sha256(const Words& input) {
    std::array<uint32_t, 8> hash{0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,
        0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19};
    std::array<uint32_t, 64> w;
    for (size_t base = 64; base < input.size(); base += 16) {
        for (int i = 0; i < 16; ++i) w[i] = input[base + i];
        for (int i = 16; i < 64; ++i) {
            auto x = w[i-15], y = w[i-2];
            auto s0 = std::rotr(x,7) ^ std::rotr(x,18) ^ (x >> 3);
            auto s1 = std::rotr(y,17) ^ std::rotr(y,19) ^ (y >> 10);
            w[i] = w[i-16] + s0 + w[i-7] + s1;
        }
        auto [a,b,c,d,e,f,g,h] = hash;
        for (int i = 0; i < 64; ++i) {
            auto s1 = std::rotr(e,6) ^ std::rotr(e,11) ^ std::rotr(e,25);
            auto t1 = h + s1 + ((e & f) ^ (~e & g)) + input[i] + w[i];
            auto s0 = std::rotr(a,2) ^ std::rotr(a,13) ^ std::rotr(a,22);
            auto t2 = s0 + ((a & b) ^ (a & c) ^ (b & c));
            h=g; g=f; f=e; e=d+t1; d=c; c=b; b=a; a=t1+t2;
        }
        std::array<uint32_t, 8> round{a,b,c,d,e,f,g,h};
        for (size_t i = 0; i < hash.size(); ++i) hash[i] += round[i];
    }
    return hash;
}

static int key(int i) { return 2 * ((i * 40503) % 65536); }

template<class Set> static int set(int size) {
    Set values;
    int score = 0;
    for (int i = 1; i <= size; ++i) score += values.insert(key(i)).second;
    for (int i = 1; i <= size; ++i) {
        if (values.contains(key(i))) score += 3;
        if (!values.contains(key(i)+1)) score += 5;
    }
    return score;
}

static int heap(int size, const std::vector<int>& ordered) {
    std::priority_queue<int,std::vector<int>,std::greater<int>> q;
    for (int i = 1; i <= size; ++i) q.push(key(i));
    int score = 0;
    for (int expected : ordered) {
        score += q.top() == expected;
        q.pop();
    }
    return score;
}

int main(int argc, char** argv) {
    if (argc < 7) return 1;
    std::string kind = argv[1], expected = argv[6];
    int size = std::stoi(argv[2]), warmups = std::stoi(argv[3]),
        samples = std::stoi(argv[4]), batch = std::stoi(argv[5]);
    Words words;
    if (kind == "sha256") {
        std::ifstream source(argv[7]);
        for (uint32_t word; source >> word;) words.push_back(word);
    }
    std::vector<int> ordered;
    if (kind == "heap") {
        for (int i = 1; i <= size; ++i) ordered.push_back(key(i));
        std::ranges::sort(ordered);
    }
    for (int s = -warmups; s < samples; ++s) {
        auto start = std::chrono::steady_clock::now();
        int score = 0;
        std::array<uint32_t, 8> digest{};
        for (int i = 0; i < batch; ++i) {
            if (kind == "tree") score = set<std::set<int>>(size);
            else if (kind == "hash") score = set<std::unordered_set<int>>(size);
            else if (kind == "heap") score = heap(size, ordered);
            else {
                digest = sha256(words);
                // Every iteration must materialize its digest, even under -O3.
                asm volatile("" : : "m"(digest) : "memory");
            }
        }
        std::chrono::duration<double,std::milli> elapsed = std::chrono::steady_clock::now() - start;
        std::ostringstream result;
        if (kind == "sha256")
            for (auto word : digest) result << std::hex << std::setfill('0') << std::setw(8) << word;
        else result << score;
        if (result.str() != expected) throw std::runtime_error(result.str());
        if (s >= 0) std::cout << std::setprecision(10) << elapsed.count()/batch << '\n';
    }
}
