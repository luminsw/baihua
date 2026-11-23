#!/usr/bin/env python3
"""Parse HarmonyOS uitest layout JSON to find tappable elements."""
import json, sys, re

def parse_bounds(b):
    m = re.match(r'\[(\d+),(\d+)\]\[(\d+),(\d+)\]', b)
    if not m: return None
    x1, y1, x2, y2 = map(int, m.groups())
    return x1, y1, x2, y2, (x1+x2)//2, (y1+y2)//2

def walk(node, results, depth=0):
    if isinstance(node, dict):
        attrs = node.get('attributes', {})
        text = attrs.get('text', '') or attrs.get('description', '') or attrs.get('accessibilityId', '')
        clickable = attrs.get('clickable', '') == 'true'
        bounds = attrs.get('bounds', '')
        typ = attrs.get('type', '')

        if (clickable or typ == 'Button' or text) and bounds:
            b = parse_bounds(bounds)
            if b:
                x1, y1, x2, y2, cx, cy = b
                if x2 > x1 and y2 > y1 and (text or clickable):
                    results.append({
                        'text': text[:50] if text else '',
                        'clickable': clickable,
                        'type': typ,
                        'bounds': bounds,
                        'cx': cx, 'cy': cy,
                        'w': x2-x1, 'h': y2-y1,
                        'page': attrs.get('pagePath', '') or attrs.get('hierarchy', '')[:30],
                    })
        for child in node.get('children', []):
            walk(child, results, depth+1)
    elif isinstance(node, list):
        for item in node:
            walk(item, results, depth+1)

if __name__ == '__main__':
    fn = sys.argv[1] if len(sys.argv) > 1 else r'C:\Users\lumin\AppData\Local\Temp\layout_huaji.xml'
    with open(fn, 'r', encoding='utf-8') as f:
        data = json.load(f)

    results = []
    walk(data, results)
    interesting = [r for r in results if r['text'] and (r['clickable'] or r['w'] > 100)]

    print(f"Total nodes: {len(results)}, interesting: {len(interesting)}")
    for r in interesting:
        flag = '+' if r['clickable'] else ' '
        print(f"  {flag} ({r['cx']:4d},{r['cy']:4d}) [{r['w']:4d}x{r['h']:3d}] [{r['type']:15s}] {r['text']}")