; weir highlighting — Helix/Zed capture names

(comment) @comment
(hash_line) @comment

(string) @string
(raw_string) @string
(interp_string) @string
(interp_text) @string
(interp_escape) @constant.character.escape
(interp_hole) @string.special

(number) @constant.numeric.integer
(boolean) @constant.builtin.boolean

(keyword) @keyword
(let_head "let" @keyword)
(let_head name: (identifier) @variable)
(type_head "type" @keyword)
(type_head name: (constructor) @type)

(constructor) @type

(attribute) @attribute

(splat) @special
(sigil) @special
(splice) @special
(bang_sigil) @special
(district_marker) @special

(operator) @operator
(punctuation) @punctuation.bracket
