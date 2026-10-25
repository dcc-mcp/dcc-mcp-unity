from pathlib import Path

from dcc_mcp_core import validate_skill

root = Path(__file__).parents[1]
skills_roots = [root / "src" / "dcc_mcp_unity" / "skills", root / "skills"]
reports = [
    validate_skill(str(path))
    for skills_root in skills_roots
    for path in skills_root.iterdir()
    if path.is_dir()
]
assert all(report.is_clean for report in reports), [report.issues for report in reports]
print(f"validated {len(reports)} bundled and optional skills")
