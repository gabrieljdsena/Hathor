import sys
import unittest

import services


class ServicesPackageTests(unittest.TestCase):
    def test_import_is_lazy(self):
        # Lightweight modules (API config/client) must be importable on a
        # machine without pygame/pywebview, which is what the tests use.
        self.assertNotIn("pygame", sys.modules)
        self.assertNotIn("webview", sys.modules)

    def test_discover_service_is_lazy(self):
        self.assertEqual(services.DiscoverService.__name__, "DiscoverService")
        self.assertNotIn("pygame", sys.modules)

    def test_unknown_attribute_is_normal_attribute_error(self):
        with self.assertRaises(AttributeError):
            services.DoesNotExist


if __name__ == "__main__":
    unittest.main()
