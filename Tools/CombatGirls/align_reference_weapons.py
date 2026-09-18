"""Compatibility entry point for reviewed 11.3.0 paint imports.

The historical combat/workbook migration is archived in the prior report and Git
history. Re-running --apply now preserves live combat tuning and all seven heroes,
including Explosher, and applies only evidence-backed paint corrections.
"""
import argparse
from paint_parity import audit, apply

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--verify-online', action='store_true')
    args = parser.parse_args()
    if args.apply:
        apply()
    audit(args.verify_online)

if __name__ == '__main__':
    main()
