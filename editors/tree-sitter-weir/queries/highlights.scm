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

; type params `'a` (external scanner; distinct from command raw strings)
(type_param) @type
(attribute) @attribute

(splat) @punctuation.special
(sigil) @punctuation.special
(splice) @punctuation.special
(bang_sigil) @punctuation.special
(district_marker) @punctuation.special

(operator) @operator
(punctuation) @punctuation.bracket

; record field DECLARATION names: an identifier/constructor immediately
; before ':' is a field member, not a type — the casing law paints
; uppercase as @type, so this override reclaims field names as @property.
; MUST precede the field-type query: both anchor on the ':' punctuation,
; and the pattern that captures ':' LAST owns the type-after-':' render
((constructor) @property . (punctuation) @_c (#eq? @_c ":"))
((identifier) @property . (punctuation) @_c (#eq? @_c ":"))

; record field types: the identifier right after ':' is a type, not a
; binder name [issue 2] — flat siblings, anchored on the ':' punctuation
((punctuation) @_colon . (identifier) @type
 (#eq? @_colon ":"))

; module members & field access: the identifier right after '.' [issue 3]
((operator) @_dot . (identifier) @property
 (#eq? @_dot "."))
