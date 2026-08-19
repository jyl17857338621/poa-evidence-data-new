#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 PDF 第一页渲染成 PNG 预览图。零依赖 PyMuPDF(fitz)。"""
import sys, os

try:
    import fitz
except Exception as e:
    print('ERR: PyMuPDF not available: ' + str(e), file=sys.stderr)
    sys.exit(2)

def render_first_page(pdf_path, out_path, max_dim=1200):
    if not os.path.exists(pdf_path):
        print('ERR: pdf not found: ' + pdf_path, file=sys.stderr)
        sys.exit(3)
    doc = fitz.open(pdf_path)
    if len(doc) == 0:
        print('ERR: empty pdf', file=sys.stderr)
        sys.exit(4)
    page = doc[0]
    # 按页面尺寸计算缩放，使最长边不超过 max_dim，保证清晰度同时控制体积
    rect = page.rect
    scale = min(max_dim / rect.width, max_dim / rect.height, 2.0)
    mat = fitz.Matrix(scale, scale)
    pix = page.get_pixmap(matrix=mat, alpha=False)
    pix.save(out_path)
    doc.close()
    print(out_path)

if __name__ == '__main__':
    if len(sys.argv) < 3:
        print('Usage: pdf_preview.py <input.pdf> <output.png> [max_dim]', file=sys.stderr)
        sys.exit(1)
    pdf_path = sys.argv[1]
    out_path = sys.argv[2]
    max_dim = int(sys.argv[3]) if len(sys.argv) > 3 else 1200
    render_first_page(pdf_path, out_path, max_dim)
