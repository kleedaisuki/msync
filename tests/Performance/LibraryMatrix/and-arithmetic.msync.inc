    bit_and: fn(left: Number, right: Number) -> Number {
        let a = left;
        let b = right;
        let place = 2147483648;
        let result = 0;
        loop {
            if a == 0 or b == 0 { break; };
            let al = a >= place;
            if al { a = a - place; };
            if b >= place {
                b = b - place;
                if al { result = result + place; };
            };
            place = place / 2;
        };
        result
    },
