#!/usr/bin/env python3
# POA 证据库 · xlsx 解析辅助脚本
# 用法: python parse_xlsx.py <file.xlsx>
# 输出: JSON 二维数组(行->列), 第一行为表头。空单元格转为空字符串。
import sys, json
import openpyxl


def main():
    if len(sys.argv) < 2:
        sys.stderr.write("usage: parse_xlsx.py <file.xlsx>\n")
        sys.exit(2)
    path = sys.argv[1]
    wb = openpyxl.load_workbook(path, data_only=True, read_only=True)
    ws = wb.worksheets[0]
    rows = []
    for r in ws.iter_rows(values_only=True):
        # read_only 工作表可能返回尾部全 None 的空行, 一并保留(导入时以 SKU 空值跳过)
        rows.append(['' if c is None else c for c in r])
    # 去掉完全为空的尾部行
    while rows and all((cell == '' or cell is None) for cell in rows[-1]):
        rows.pop()
    print(json.dumps(rows, ensure_ascii=False))


if __name__ == '__main__':
    main()
