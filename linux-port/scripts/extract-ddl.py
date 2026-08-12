"""Extracts CREATE TABLE / CREATE INDEX statements from C# source.

Used by verify-schema.sh to compare this port's schema against upstream's. Both files keep
their DDL as string literals -- upstream in a Dictionary, this port in a tuple array -- so the
comparison has to be between the statements themselves, not the C# wrapping them.

Handles raw literals (\"\"\"...\"\"\") and ordinary ones alike. Optional second and third
arguments window the search, so the port's own additive tables can be left out of its side.
"""
import re
import sys

source = open(sys.argv[1]).read()
begin = sys.argv[2] if len(sys.argv) > 2 else ""
finish = sys.argv[3] if len(sys.argv) > 3 else ""

if begin:
    source = source[source.index(begin):]
if finish:
    source = source[:source.index(finish)]

statements = []


def keep(text):
    if re.match(r"\s*CREATE\s+(TABLE|INDEX)\b", text, re.I):
        statements.append(text.strip())


# Raw literals first, then blank them out so the ordinary-literal pass cannot see inside one.
for match in re.finditer(r'"""(.*?)"""', source, re.S):
    keep(match.group(1))
source = re.sub(r'""".*?"""', '""', source, flags=re.S)

for match in re.finditer(r'"((?:[^"\\]|\\.)*)"', source):
    keep(match.group(1))

for statement in statements:
    print(statement.replace("\n", " "))
