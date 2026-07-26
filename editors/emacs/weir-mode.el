;;; weir-mode.el --- major mode for weir scripts -*- lexical-binding: t; -*-

;; Minimal on purpose: a prog-mode derivative to hang the language
;; association on (Emacs attaches servers to major modes), comment
;; syntax matching `weir fmt`, and an eglot entry for `weir lsp`.
;; No font-lock rules: highlighting beyond LSP semantic tokens is
;; tree-sitter's job (fast-follow).

(define-derived-mode weir-mode prog-mode "weir"
  "Major mode for weir scripts."
  (setq-local comment-start "// "
              comment-end ""
              indent-tabs-mode nil
              tab-width 4))

;; .weir files, and extensionless scripts with a weir shebang
(add-to-list 'auto-mode-alist '("\\.weir\\'" . weir-mode))
(add-to-list 'interpreter-mode-alist '("weir" . weir-mode))

(with-eval-after-load 'eglot
  (add-to-list 'eglot-server-programs '(weir-mode . ("weir" "lsp"))))

(provide 'weir-mode)
;;; weir-mode.el ends here
