from __future__ import annotations

from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]


class DistributionLicenseTests(unittest.TestCase):
    """功能：防止 Community 发行许可意外回退为宽松商业许可证。

    作者：高宏顺
    邮箱：18272669457@163.com
    """

    def test_community_is_noncommercial_and_cargo_uses_license_file(self) -> None:
        """功能：验证标准非商业正文、Required Notice、联系方式和 Cargo 元数据。

        作者：高宏顺
        邮箱：18272669457@163.com
        """

        license_text = (ROOT / "LICENSE").read_text(encoding="utf-8")
        cargo_text = (ROOT / "rust/Cargo.toml").read_text(encoding="utf-8")
        readme_text = (ROOT / "README.md").read_text(encoding="utf-8")
        self.assertIn("PolyForm Noncommercial License 1.0.0", license_text)
        self.assertIn("Required Notice:", license_text)
        self.assertIn("18272669457@163.com", license_text)
        self.assertIn("Any commercial use requires a separate written commercial license", license_text)
        self.assertIn('license-file = "../LICENSE"', cargo_text)
        self.assertNotIn('license = "MIT"', cargo_text)
        self.assertIn("仅限非商业使用", readme_text)
        self.assertIn("商业授权", readme_text)


if __name__ == "__main__":
    unittest.main()

