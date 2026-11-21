import ast
import unittest
from pathlib import Path


class PlaybackSourceTests(unittest.TestCase):
    def test_download_history_is_a_restorable_queue_source(self):
        source = (Path(__file__).resolve().parents[1] / "services" / "playback.py").read_text(
            encoding="utf-8"
        )
        tree = ast.parse(source)
        values = None
        for node in tree.body:
            if not isinstance(node, ast.ClassDef) or node.name != "PlaybackController":
                continue
            for item in node.body:
                if not isinstance(item, ast.Assign):
                    continue
                if any(
                    isinstance(target, ast.Name) and target.id == "VALID_SOURCE_TYPES"
                    for target in item.targets
                ):
                    values = ast.literal_eval(item.value)
        self.assertIsNotNone(values, "VALID_SOURCE_TYPES not found")
        self.assertIn("download_history", values)
        self.assertIn("recently_played", values)
        self.assertIn("ai_dj", values)


if __name__ == "__main__":
    unittest.main()
