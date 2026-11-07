"""Export small factual capability tables; never reads/copies client implementation bytes.

Input is the internal, independently verified research census. --check is read-only.
No network, JAR, mapping, asset, bytecode or absolute input path enters the output.
"""
import argparse
import hashlib
import json
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
OUTPUT = REPO / "src/RZeroF1.Minecraft.Projection/Capabilities/vanilla-capabilities.json"
FAMILIES = {
    "容器部件模型": "ContainerParts", "版本特有实体床": "EntityBed",
    "染色多层表面": "LayeredSurface", "头部与外观资产": "HeadAppearance",
    "文字与支架组合": "TextAndBoard", "共享书本模型": "Book",
    "枢轴部件模型": "PivotParts", "多面材质模型": "MultiFaceMaterial",
    "姿态模型图": "PoseGraph", "嵌入物品显示": "EmbeddedItem",
    "嵌入实体显示": "EmbeddedEntity", "普通模型变换重用": "MovedBlock",
    "程序化光束": "Beam", "门户程序化材质": "Portal",
    "世界状态多部件多通道": "WorldParts", "调试线框": "DebugOverlay",
}
# Explicit class-key join for the mapped pre-Type registry. Not a Block-ID heuristic.
CLASS_TYPES = dict(zip([
    "SignBlockEntity", "SpawnerBlockEntity", "PistonMovingBlockEntity", "ChestBlockEntity",
    "EnderChestBlockEntity", "EnchantmentTableBlockEntity", "LecternBlockEntity",
    "TheEndPortalBlockEntity", "TheEndGatewayBlockEntity", "BeaconBlockEntity", "SkullBlockEntity",
    "BannerBlockEntity", "StructureBlockEntity", "ShulkerBoxBlockEntity", "BedBlockEntity",
    "ConduitBlockEntity", "BellBlockEntity", "CampfireBlockEntity",
], ["sign", "mob_spawner", "piston", "chest", "ender_chest", "enchanting_table", "lectern",
    "end_portal", "end_gateway", "beacon", "skull", "banner", "structure_block", "shulker_box",
    "bed", "conduit", "bell", "campfire"]))
# These are canonical SDK recipe keys, not claims that obfuscated legacy classes have
# these Java names (legacy rows deliberately keep provider=null).
LEGACY_RECIPES = dict(zip(CLASS_TYPES.values(), [
    "SignRenderer", "SpawnerRenderer", "PistonHeadRenderer", "ChestRenderer", "ChestRenderer",
    "EnchantTableRenderer", "LecternRenderer", "TheEndPortalRenderer", "TheEndGatewayRenderer",
    "BeaconRenderer", "SkullBlockRenderer", "BannerRenderer", "StructureBlockRenderer",
    "ShulkerBoxRenderer", "BedRenderer", "ConduitRenderer", "BellRenderer", "CampfireRenderer",
]))
# Closed endpoint sets. Adding an audited version requires an explicit profile
# decision, rather than changing the meaning of an ordinal or open-ended range.
ENDPOINTS = [
    "a1.1.0", "1.2.5", "1.3.2", "1.4.7", "1.5.2", "1.6.4", "1.7.10", "1.8.9",
    "1.9.4", "1.10.2", "1.11", "1.11.2", "1.12", "1.12.2", "1.13", "1.13.2",
    "1.14", "1.14.4", "1.15", "1.15.2", "1.16", "1.16.1", "1.16.5", "1.17",
    "1.17.1", "1.18", "1.18.2", "1.19", "1.19.2", "1.19.4", "1.20", "1.20.6",
    "1.21", "1.21.1", "1.21.3", "1.21.4", "1.21.5", "1.21.11", "26.1", "26.1.2",
    "26.2", "26.3", "26.4-snapshot-6",
]
PRE_FLATTENING_JSON = {"1.8.9", "1.9.4", "1.10.2", "1.11", "1.11.2", "1.12", "1.12.2"}
PRE_ENTITY_BED = {"a1.1.0", "1.2.5", "1.3.2", "1.4.7", "1.5.2", "1.6.4", "1.7.10", "1.8.9", "1.9.4", "1.10.2", "1.11", "1.11.2"}
DOUBLE_CHEST = {"1.11", "1.11.2", "1.12", "1.12.2", "1.13", "1.13.2", "1.14", "1.14.4"}
LEGACY_CHEST = {"a1.1.0", "1.2.5", "1.3.2", "1.4.7", "1.5.2", "1.6.4", "1.7.10", "1.8.9", "1.9.4", "1.10.2"}


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def digest(value):
    return hashlib.sha256(canonical(value).encode()).hexdigest()


def build(root):
    def load(name):
        return json.loads((root / name).read_text(encoding="utf-8"))

    clients = load("evidence/clients.json")
    rows = load("evidence/registration-catalog.json")
    types = load("evidence/block-entity-types.json")
    summary = load("evidence/version-summary.json")
    providers = load("evidence/provider-families.json")
    assert len(rows) == 762 and len(providers) == 28 and len(FAMILIES) == 16
    assert len({c["sha256"] for c in clients}) == len(summary) == 43
    assert [s["version"] for s in summary] == ENDPOINTS, "New endpoint requires an explicit audited profile decision"
    assert all(c["officialSha1Matched"] for c in clients)
    tables = {"registrations": {}, "types": {}, "codecs": {}}

    def intern(table, value):
        key = digest(value)[:16]
        if key in tables[table]:
            assert tables[table][key] == value, "table digest collision"
        tables[table][key] = value
        return key

    versions = []
    for s in summary:
        version = s["version"]
        client = next(c for c in clients if c["id"] == version)
        registrations = []
        for row in [r for r in rows if r["version"] == version]:
            type_id = row["typeId"]
            if "/" in type_id:
                type_id = CLASS_TYPES[type_id.rsplit("/", 1)[1]]
            assert len(row["providers"]) == 1
            provider = row["providers"][0]
            named = provider.rsplit("/", 1)[1] if "/" in provider else None
            recipe = named or LEGACY_RECIPES[type_id]
            assert providers[recipe]["family"] == row["family"]
            registrations.append(["minecraft:" + type_id, named, recipe])
        registrations.sort()
        version_types = sorted("minecraft:" + t["id"] for t in types if t["version"] == version)
        assert len(version_types) == len(set(version_types))
        # Shared subsystem censuses exist only at audited endpoints. Missing is unknown,
        # never an inherited claim that a later/earlier version has the same codecs.
        shared = root / f"evidence/shared-systems/{version}.json"
        codecs = None
        if shared.exists():
            codecs = sorted(set(
                x["registry"].rsplit("/", 1)[1] + ":" + x["id"]
                for x in json.loads(shared.read_text(encoding="utf-8"))["resourceTypeRegistrations"]))
        numeric = bool(s["legacyNumericDispatcher"])
        modern_boards = version in ["26.2", "26.3", "26.4-snapshot-6"]
        # Endpoints only: no semantic-version range inference.
        profile = {
            "routing": "LegacyClass" if not s["officialMapping"] and not s["unobfuscated"] else
                "Class" if version == "1.14.4" else "Bootstrap" if version == "26.4-snapshot-6" else "Type",
            "resources": "TerrainAtlas" if numeric else "LegacyNames" if version in PRE_FLATTENING_JSON else "BlockJson",
            "boards": "BlockModel" if modern_boards else "PartModel",
            "bed": "BlockModel" if modern_boards or version in PRE_ENTITY_BED else "ColorNbt" if version in ["1.12", "1.12.2"] else "ColorId",
            "chest": "Double128" if version in DOUBLE_CHEST else "Legacy" if version in LEGACY_CHEST else "Split64",
            "data": "SourceDataVersionRequired",
            "material": "AuditedRequirementsOnly",
        }
        versions.append({
            "id": version, "clientSha256": client["sha256"], "profile": profile,
            "registrations": intern("registrations", registrations),
            "types": intern("types", version_types) if version_types else None,
            "codecs": intern("codecs", codecs) if codecs is not None else None,
            "codecEvidenceSha256": hashlib.sha256(shared.read_bytes()).hexdigest() if shared.exists() else None,
            "registrationCount": len(registrations),
            "registrationEvidenceSha256": digest([r for r in rows if r["version"] == version]),
            "evidence": "MappedRegistration" if s["officialMapping"] or s["unobfuscated"] else "StructuralNbtJoin",
        })
    return {
        "schema": 1,
        "evidenceId": "vanilla-render-census-2026-11-07",
        "sourceDigests": {n: hashlib.sha256((root / "evidence" / n).read_bytes()).hexdigest()
                          for n in ["clients.json", "registration-catalog.json", "block-entity-types.json", "provider-families.json", "version-summary.json"]},
        "providers": {p: FAMILIES[v["family"]] for p, v in sorted(providers.items())},
        "versions": versions, **tables,
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("research", type=Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    text = json.dumps(build(args.research), ensure_ascii=False, indent=2) + "\n"
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text(encoding="utf-8") != text:
            raise SystemExit("FAIL capability spec differs from verified census; review before regenerating")
    else:
        OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        OUTPUT.write_text(text, encoding="utf-8", newline="\n")
    print(f"PASS 43 clients / 762 registrations / 28 providers / 16 families; {len(text.encode())} spec bytes")
