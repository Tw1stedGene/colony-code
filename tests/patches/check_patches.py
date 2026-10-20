#!/usr/bin/env python3
"""Sanity check for mod/Patches: every PatchOperationAdd XPath must match the game's XML, and every
genStep it adds must be a GenStepDef of the game or the mod.

Usage: python3 tests/patches/check_patches.py [GameDataDir]
GameDataDir defaults to the local macOS Steam install's Data folder (Core + DLC folders).
Needs lxml (pip install lxml). Exits 1 on any problem.
"""
import glob
import os
import sys

try:
    from lxml import etree
except ImportError:
    sys.exit("check_patches: needs lxml (pip install lxml)")

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DATA = sys.argv[1] if len(sys.argv) > 1 else \
    "/Volumes/SSD/SteamLibrary/steamapps/common/RimWorld/RimWorldMac.app/Data"


def combined_defs(files):
    """All Defs files merged under one <Defs> root, as RimWorld does before patching."""
    root = etree.Element("Defs")
    for f in files:
        try:
            doc = etree.parse(f)
        except etree.XMLSyntaxError as e:
            print("skip unparsable %s: %s" % (f, e))
            continue
        for el in doc.getroot():
            if isinstance(el.tag, str):
                root.append(el)
    return etree.ElementTree(root)


game_files = sorted(glob.glob(os.path.join(DATA, "*", "Defs", "**", "*.xml"), recursive=True))
mod_files = sorted(glob.glob(os.path.join(ROOT, "mod", "Defs", "**", "*.xml"), recursive=True))
if not game_files:
    sys.exit("check_patches: no game Defs under %s" % DATA)
game = combined_defs(game_files)
everything = combined_defs(game_files + mod_files)
genstep_names = set(everything.xpath("/Defs/GenStepDef/defName/text()"))

problems = 0
ops = 0
for patch in sorted(glob.glob(os.path.join(ROOT, "mod", "Patches", "*.xml"))):
    for op in etree.parse(patch).getroot().iter("Operation"):
        ops += 1
        cls = op.get("Class")
        xpath = op.findtext("xpath")
        name = "%s: %s %s" % (os.path.basename(patch), cls, xpath)
        if cls != "PatchOperationAdd":
            print("SKIP %s (only PatchOperationAdd is checked)" % name)
            continue
        matches = game.xpath("/" + xpath.lstrip("/"))
        if not matches:
            print("FAIL %s: matches nothing in the game's Defs" % name)
            problems += 1
            continue
        print("ok   %s: %d match(es)" % (name, len(matches)))
        if xpath.rstrip("/").endswith("/genSteps"):
            for li in op.find("value").iter("li"):
                if li.text not in genstep_names:
                    print("FAIL %s: GenStepDef %s not defined" % (name, li.text))
                    problems += 1
                else:
                    print("ok   GenStepDef %s exists" % li.text)
                for m in matches:
                    present = [x.text for x in m.findall("li")]
                    if li.text in present:
                        print("FAIL %s: %s already in the target list" % (name, li.text))
                        problems += 1

print("check_patches: %d operation(s), %d problem(s)" % (ops, problems))
sys.exit(1 if problems else 0)
