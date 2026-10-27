//! Small, deterministic spelling suggestions; never a reason to change input.

pub fn closest<'a>(input: &str, names: impl IntoIterator<Item = &'a str>) -> Option<&'a str> {
    let length = input.chars().count();
    names
        .into_iter()
        .filter(|name| name.chars().count().abs_diff(length) <= 2)
        .map(|name| (distance(input, name), name))
        .filter(|(cost, _)| *cost <= 2)
        .min_by_key(|(cost, name)| (*cost, *name))
        .map(|(_, name)| name)
}

pub fn distance(left: &str, right: &str) -> usize {
    let right: Vec<_> = right.chars().collect();
    let mut row: Vec<_> = (0..=right.len()).collect();
    for (index, left) in left.chars().enumerate() {
        let mut diagonal = row[0];
        row[0] = index + 1;
        for (column, right) in right.iter().enumerate() {
            let old = row[column + 1];
            row[column + 1] = (row[column] + 1)
                .min(old + 1)
                .min(diagonal + usize::from(left != *right));
            diagonal = old;
        }
    }
    row[right.len()]
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn suggestions_are_bounded_unicode_aware_and_deterministic() {
        assert_eq!(closest("/hep", ["/help", "/pwd"]), Some("/help"));
        assert_eq!(closest("--job", ["--jobs", "--help"]), Some("--jobs"));
        assert_eq!(closest("unrelated-name", ["/help"]), None);
        assert_eq!(distance("界面", "界"), 1);
    }
}
