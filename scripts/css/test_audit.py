import unittest
import tempfile
from pathlib import Path
from unittest.mock import patch
from audit import prune, remove_retired_selectors, source_inventory, ownership_report, selected_inputs, selector_classes, main
import json
import sys


class CascadeSafetyTests(unittest.TestCase):
    def test_anonymous_layers_keep_distinct_cascade_positions(self):
        css = '@layer {.x {color:red;}} .x {color:blue;} @layer {.x {color:red;}}'
        self.assertEqual(css, prune(css)[0])

    def test_retains_browser_fallback_and_priority(self):
        css = '.x { height: 100vh; height: 100dvh; color: red !important; color: red; }'
        result, deleted, errors = prune(css)
        self.assertEqual(css, result)
        self.assertFalse(deleted or errors)

    def test_duplicate_requires_all_group_members_and_same_condition(self):
        css = '.a,.b { color: red; } .a { color: red; } @media (hover:hover) {.b {color:red;}}'
        self.assertEqual(css, prune(css)[0])
        css += ' .b {color:red;}'
        result, deleted, errors = prune(css)
        self.assertEqual(1, len(deleted))
        self.assertIn('@media (hover:hover)', result)
        self.assertFalse(errors)

    def test_keyframes_and_quoted_delimiters_survive(self):
        css = '@keyframes k {0% { opacity:0; } 100% { opacity:0; }} .a {content:"};";content:"};";}'
        result, deleted, errors = prune(css)
        self.assertIn('@keyframes k {0% { opacity:0; } 100% { opacity:0; }}', result)
        self.assertEqual(1, len(deleted))
        self.assertIn('content:"};";', result)
        self.assertFalse(errors)

    def test_final_declaration_with_quoted_brace_is_preserved(self):
        css = '.a {content:"}"} .a {content:"}"}'
        result, deleted, errors = prune(css)
        self.assertEqual(css, result)
        self.assertFalse(deleted or errors)

    def test_negative_and_alternative_selectors_are_not_dead(self):
        css = '.live:not(.retired) {color:red;} :is(.retired,.live) {color:blue;} .retired,.live {color:green;}'
        result, deleted = remove_retired_selectors(css, {'retired'})
        self.assertIn('.live:not(.retired)', result)
        self.assertIn(':is(.retired,.live)', result)
        self.assertIn('.live {color:green;}', result)
        self.assertEqual(['.retired'], [entry['selector'] for entry in deleted])


class OwnershipTests(unittest.TestCase):
    def test_line_cap_includes_unsupported_isolated_files_but_excludes_globals(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / 'src'
            source.mkdir()
            isolated, global_css = source / 'Broken.razor.css', source / 'app.css'
            isolated.write_text('.x {\n' + '\n' * 9 + 'color red;}', encoding='utf-8')
            global_css.write_text('\n' * 30, encoding='utf-8')
            report = root / 'audit.json'
            args = ['audit.py', '--max-lines', '10', '--output', str(report)]
            with patch('audit.ROOT', root), patch('audit.inputs', return_value=[isolated, global_css]), patch.object(sys, 'argv', args):
                self.assertEqual(1, main())
            data = json.loads(report.read_text(encoding='utf-8'))
            self.assertEqual(11, data['files'][0]['lines'])
            self.assertTrue(data['files'][0]['isolated'])
            self.assertTrue(data['files'][0]['errors'])
            self.assertFalse(data['files'][1]['isolated'])

    def test_pruning_without_explicit_file_selection_is_rejected(self):
        with patch.object(sys, 'argv', ['audit.py', '--prune', '--output', 'unused.json']):
            with self.assertRaises(SystemExit) as error:
                main()
            self.assertEqual(2, error.exception.code)

    def report(self, sources, selector):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name, content in sources.items():
                (root / name).write_text(content, encoding='utf-8')
            inventory = source_inventory(root)
            return ownership_report(root / 'Parent.razor.css', selector + ' { color:red; }', inventory, root)

    def test_child_literal_owner_is_a_candidate_requiring_dom_review(self):
        report = self.report({'Child.razor': '<div class="child"></div>'}, '.parent ::deep .child')
        target = report['selectors'][0]['targets'][0]
        self.assertEqual('child:Child.razor', target['owner_status'])
        self.assertFalse(target['emitted_scope_verified'])

    def test_shared_literal_and_third_party_targets(self):
        report = self.report({'Child.razor': '<div class="shared"></div>',
                              'Other.razor': '<span class="shared"></span>'}, '::deep .shared .mud-switch')
        targets = {t['class']: t for t in report['selectors'][0]['targets']}
        self.assertTrue(targets['shared']['owner_status'].startswith('shared:'))
        self.assertEqual('third-party', targets['mud-switch']['owner_status'])

    def test_component_parameter_and_renderer_are_not_literal_owners(self):
        report = self.report({'Child.razor': '<AppCssElement Class="generated" />',
                              'Renderer.cs': 'void BuildRenderTree() { var value = "generated"; }'}, '::deep .generated')
        target = report['selectors'][0]['targets'][0]
        self.assertEqual('unresolved', target['owner_status'])
        self.assertEqual(2, len(target['candidates']))

    def test_dynamic_classes_are_unresolved_and_reported(self):
        report = self.report({'Child.razor': '<div class="@($"dynamic-{Kind}")"></div>'}, '::deep .dynamic-book')
        self.assertEqual('unresolved', report['selectors'][0]['targets'][0]['owner_status'])
        self.assertEqual('dynamic-prefix', report['selectors'][0]['targets'][0]['candidates'][0]['kind'])

    def test_selector_functions_not_attribute_values_are_classes(self):
        self.assertEqual(['a', 'b', 'c'], selector_classes('.a:is(.b,.c)[title=".not-a-class"]'))

    def test_selected_inputs_cannot_prune_other_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first, second = root / 'First.razor.css', root / 'Second.razor.css'
            for path in (first, second):
                path.write_text('.x {color:red;color:red;}', encoding='utf-8')
            with patch('audit.inputs', return_value=[first, second]):
                selected = selected_inputs(['First.razor.css'], root)
                self.assertEqual([first], selected)
                for path in selected:
                    path.write_text(prune(path.read_text())[0], encoding='utf-8')
                self.assertEqual('.x {color:red;color:red;}', second.read_text())
                with self.assertRaises(ValueError):
                    selected_inputs(['../Outside.css'], root)


if __name__ == '__main__':
    unittest.main()
