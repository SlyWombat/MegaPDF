"""Tests for the derivation tables in tools/gen_strings.py (#724).

    python3 -m unittest tools/test_gen_strings.py

Standard library only. Both derivations share the same shape (a table of
(source, derived) word pairs), so the per-row checks cover both.
"""

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import gen_strings as g  # noqa: E402


class SpanishDerivation(unittest.TestCase):
    def test_each_es_row(self):
        for a, b in g.ES_419_TO_ES_ES:
            with self.subTest(row=a):
                self.assertEqual(g.to_spain(f"Abre {a} ahora"), f"Abre {b} ahora")

    def test_es_whole_words(self):
        self.assertEqual(g.to_spain("videoteca"), "videoteca")
        self.assertEqual(g.to_spain("llenarlo"), "llenarlo")

    def test_es_article_agreement(self):
        self.assertEqual(g.to_spain("Guarda la computadora"), "Guarda el ordenador")
        self.assertEqual(g.to_spain("las computadoras"), "los ordenadores")

    def test_es_no_space_inserted(self):
        self.assertEqual(g.to_spain("¿Guardar los cambios?"), "¿Guardar los cambios?")
        self.assertNotIn(" ", g.to_spain("Página: 1; ¡Listo!"))

    def test_es_placeholders_untouched(self):
        for text in ("%1$s de %2$d", "{0} de {1}", "%@ firmas", "%lld páginas", "{name} - editado"):
            with self.subTest(text=text):
                self.assertEqual(g.to_spain(text), text)

    def test_es_idempotent(self):
        for a, _ in g.ES_419_TO_ES_ES:
            with self.subTest(row=a):
                once = g.to_spain(a)
                self.assertEqual(g.to_spain(once), once)

    def test_es_demo_name_and_file_follow_each_other(self):
        self.assertEqual(g.to_spain("Sofía Ramírez"), "Lucía Fernández")
        self.assertEqual(g.to_spain("demo-es"), "demo-es-ES")
        self.assertEqual(g.to_spain("demo-es-ES"), "demo-es-ES")

    def test_settings_is_by_key_not_by_word(self):
        # The sentence keeps its word: only the keys in ES_ES_KEY_OVERRIDES change.
        self.assertEqual(g.to_spain("la configuración de seguridad"), "la configuración de seguridad")
        self.assertEqual(g.to_spain("Configuración"), "Configuración")
        self.assertEqual(g.ES_ES_KEY_OVERRIDES["android"], {"settings": "Ajustes"})
        self.assertEqual(g.ES_ES_KEY_OVERRIDES["ios"], {"Settings": "Ajustes", "Settings…": "Ajustes…"})


class FrenchDerivation(unittest.TestCase):
    def test_each_fr_row(self):
        for ca, fr in g.FR_CA_TO_FR_FR[:-1]:  # the last row is the identity row
            with self.subTest(row=ca):
                self.assertEqual(g.to_france(f"Ouvrez {ca} maintenant"), f"Ouvrez {fr} maintenant")


if __name__ == "__main__":
    unittest.main()
