// tree-sitter-weir — a RENDERER, not a second parser.
//
// The one truth for what weir accepts is `weir check`'s pipeline
// (SEMANTICS.md); this grammar's only job is better-than-grey
// highlighting in tree-sitter consumers (Helix, Zed, forges). It
// over-accepts freely and does not replicate the assembler's
// logical-line reconstruction — a continuation line may highlight as
// a fresh statement. Never cite it as the language definition.

module.exports = grammar({
  name: 'weir',

  extras: $ => [/[ \t\r\n]/, $.comment],

  // external scanner: `'a` (type param) vs `'echo x'` (command raw string)
  externals: $ => [$.type_param],

  word: $ => $.identifier,

  rules: {
    source_file: $ => repeat($._item),

    _item: $ =>
      choice(
        $.hash_line,
        $.attribute,
        $.let_head,
        $.type_head,
        $.keyword,
        $.boolean,
        $.interp_string,
        $.raw_verbatim,
        $.string,
        $.type_param,
        $.raw_string,
        $.splat,
        $.sigil,
        $.splice,
        $.bang_sigil,
        $.district_marker,
        $.number,
        $.constructor,
        $.identifier,
        $.operator,
        $.punctuation,
        $.stray,
      ),

    // `//` comments; `//` mid-token is NOT a comment in weir (URLs
    // pass through) — approximated here by requiring a boundary
    // before, via extras ordering (good enough for a renderer)
    comment: _ => token(seq('//', /.*/)),

    // shebang and mode lines (`#!/usr/bin/env weir`, `#loose`)
    hash_line: _ => token(prec(1, seq('#', /.*/))),

    attribute: _ => token(choice('[<', '>]')),

    // higher precedence than the bare `let`/`type` keyword fallback
    // (the fallback still fires for `let (a, b) = ...` patterns)
    let_head: $ =>
      prec.right(2, seq('let', field('name', choice($.identifier, '_')))),

    type_head: $ => prec.right(2, seq('type', field('name', $.constructor))),

    keyword: _ =>
      choice(
        'in',
        'fun',
        'match',
        'with',
        'type',
        'let',
        'of',
        'if',
        'then',
        'elif',
        'else',
        'when',
        'from',
      ),

    boolean: _ => choice('true', 'false'),

    string: _ =>
      token(seq('"', repeat(choice(/[^"\\]/, /\\./)), '"')),

    // @"..." verbatim [D:raw-strings]: a backslash is an ORDINARY char (NOT
    // an escape — the regular-string `\\.` rule does not belong here), the
    // ONLY escape is "" for a literal quote, and the string closes on the
    // first lone ". Without this, @"\" mis-scanned as an escaped quote and
    // swallowed the rest of the file (Zed/tree-sitter only; TextMate had it).
    raw_verbatim: _ => token(seq('@"', repeat(choice(/[^"]/, '""')), '"')),

    raw_string: _ => token(seq("'", /[^'\n]*/, "'")),

    // $"text {hole} text" — holes hold expressions; loose interior
    interp_string: $ =>
      seq(
        '$"',
        repeat(choice($.interp_text, $.interp_escape, $.interp_hole)),
        token.immediate('"'),
      ),

    interp_text: _ => token.immediate(prec(1, /[^"{}\\]+/)),
    interp_escape: _ => token.immediate(choice('{{', '}}', /\\./)),
    // holes hold expressions, quoted strings included
    interp_hole: _ =>
      token.immediate(seq('{', repeat(choice(/[^}"]/, seq('"', /[^"]*/, '"'))), '}')),

    // $@name / $@( — the argv splat
    splat: _ =>
      token(choice(/\$@[A-Za-z_][A-Za-z0-9_]*/, '$@(')),

    // $( capture opener; $e( env-capture (glued)
    sigil: _ => token(choice('$(', /\$[A-Za-z_][A-Za-z0-9_]*\(/)),

    // $name splice (argv / interpolation-adjacent)
    splice: _ => token(/\$[A-Za-z_][A-Za-z0-9_]*/),

    // !( effect opener; !e( env-effect (glued)
    bang_sigil: _ => token(choice('!(', /![A-Za-z_][A-Za-z0-9_]*\(/)),

    // line-end district markers `!` / `!name` (loosely: a lone bang)
    district_marker: _ => token(prec(-1, /![A-Za-z_0-9]*/)),

    number: _ => token(/\d+/),

    constructor: _ => token(/[A-Z][A-Za-z0-9_]*/),

    identifier: _ => token(/[a-z_][A-Za-z0-9_]*/),

    operator: _ =>
      choice(
        '|>',
        '>>',
        '->',
        '::',
        '==',
        '!=',
        '<=',
        '>=',
        '&&',
        '||',
        '..',
        '|',
        '=',
        '<',
        '>',
        '+',
        '-',
        '*',
        '/',
        '%',
        '^',
        '.',
        '_',
      ),

    punctuation: _ => choice('(', ')', '[', ']', '{', '}', ';', ',', ':'),

    // single-char fallback: command argv holds nearly any character
    // (--flags, paths, globs, ~, ?) — a stray never becomes ERROR
    stray: _ => token(prec(-2, /[^ \t\r\n]/)),
  },
});
