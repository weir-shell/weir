; weir highlighting — Helix/Zed capture names

(comment) @comment
(hash_line) @comment

; /// doc comments colour distinctly from // — the third slash. A
; query override (comment token already spans the whole run); placed
; after the plain @comment so last-wins reclaims the /// ones.
((comment) @comment.documentation (#match? @comment.documentation "^///"))

(string) @string
(raw_verbatim) @string
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
(within_head "within" @keyword)
(within_head kind: (within_kind) @keyword)

(constructor) @type

; the four lowercase builtin types (int/string/bool/unit). The casing law
; makes every OTHER type uppercase (a @type constructor), so this closed set
; is the whole lowercase-type universe; catches them in EVERY position
; (of/-> /generic args) the `:`-anchored field-type query cannot reach.
; Placed EARLY so the later position overrides (field-name, member) win for
; the rare collision (a member/field named like a builtin).
((identifier) @type (#any-of? @type "int" "string" "bool" "unit"))

; type params `'a` (external scanner; distinct from command raw strings)
(type_param) @type
(attribute) @attribute

(splat) @punctuation.special
(sigil) @punctuation.special
(splice) @punctuation.special
(bang_sigil) @punctuation.special

; the yaml district [D:yaml-district]: the marker word reads as a
; keyword; keys are members, scalar text is data (string); the
; `to yaml`/`from yaml` adapter is one token, keyword-coloured whole
(yaml_marker) @keyword
(yaml_key) @property
(yaml_text) @string
(yaml_for) @keyword
(yaml_hole) @punctuation.special
(adapter) @keyword

(operator) @operator
(punctuation) @punctuation.bracket

; record field DECLARATION names: an identifier/constructor immediately
; before ':' is a field member, not a type — the casing law paints
; uppercase as @type, so this override reclaims field names as @property.
; MUST precede the field-type query: both anchor on the ':' punctuation,
; and the pattern that captures ':' LAST owns the type-after-':' render
((constructor) @property . (punctuation) @_c (#eq? @_c ":"))
((identifier) @property . (punctuation) @_c (#eq? @_c ":"))

; record CONSTRUCTION field names: a constructor immediately before '=' is a
; field being assigned (`{ BicepPath = ... }`), not a type. Only the
; constructor (uppercase) form — the casing law is what mis-paints those as
; @type; a lowercase field reads as a plain identifier already, and matching
; identifiers here would wrongly reclaim a let-binding's last PARAM before '='.
; The '=' must be exactly one token (#eq? excludes '==' comparisons), and the
; anchor to an adjacent-sibling operator excludes `type X =` and `let x =`
; (their name sits INSIDE a type_head/let_head node, not a sibling of the '=').
((constructor) @property . (operator) @_eq (#eq? @_eq "="))

; record field types: the identifier right after ':' is a type, not a
; binder name [issue 2] — flat siblings, anchored on the ':' punctuation
((punctuation) @_colon . (identifier) @type
 (#eq? @_colon ":"))

; module members & field access: the identifier right after '.' [issue 3]
((operator) @_dot . (identifier) @property
 (#eq? @_dot "."))

; a PascalCase field access (`ctx.Repo`) tokenizes as a constructor after
; '.', so the casing law paints it @type — reclaim it as a member. An
; identifier immediately after '.' is a member regardless of case.
((operator) @_dot . (constructor) @property
 (#eq? @_dot "."))
