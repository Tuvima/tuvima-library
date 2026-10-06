import unittest
from audit import prune, remove_retired_selectors


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


if __name__ == '__main__':
    unittest.main()
