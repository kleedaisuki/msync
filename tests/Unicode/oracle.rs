use std::io::{self, BufRead, Write};

fn hex(value: &str) -> String {
    value
        .chars()
        .map(|c| format!("{:X}", c as u32))
        .collect::<Vec<_>>()
        .join(" ")
}

fn parse(line: &str) -> String {
    line.split_whitespace()
        .map(|part| char::from_u32(u32::from_str_radix(part, 16).unwrap()).unwrap())
        .collect()
}

fn main() -> io::Result<()> {
    assert_eq!(char::UNICODE_VERSION, (17, 0, 0));
    let stdout = io::stdout();
    let mut output = io::BufWriter::new(stdout.lock());
    if std::env::args().nth(1).as_deref() == Some("--scalars") {
        for scalar in 0..=0x10ffff {
            let Some(character) = char::from_u32(scalar) else {
                continue;
            };
            let value = character.to_string();
            let lower = value.to_lowercase();
            let upper = value.to_uppercase();
            if value != lower || value != upper {
                writeln!(output, "{scalar:X};{};{}", hex(&lower), hex(&upper))?;
            }
        }
        return Ok(());
    }
    let order = std::env::args().nth(1).as_deref() == Some("--order");
    for line in io::stdin().lock().lines() {
        let line = line?;
        if order {
            let (left, right) = line.split_once(';').unwrap();
            let result = match parse(left).cmp(&parse(right)) {
                std::cmp::Ordering::Less => -1,
                std::cmp::Ordering::Equal => 0,
                std::cmp::Ordering::Greater => 1,
            };
            writeln!(output, "{result}")?;
            continue;
        }
        let value = parse(&line);
        writeln!(
            output,
            "{};{}",
            hex(&value.to_lowercase()),
            hex(&value.to_uppercase())
        )?;
    }
    Ok(())
}
