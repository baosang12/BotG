#!/usr/bin/env python3
"""Schema guard and artifact validator for Gate24h runs.

Validates 6 required files with proper column schemas, row counts, and configuration checks.
"""

import argparse
import csv
import json
import sys
from pathlib import Path
from typing import Dict, List, Optional, Set, Any


REQUIRED_FILES = {
    "orders.csv": {
        "required_columns": {
            "phase", "timestamp_iso", "orderId", "side", "execPrice", 
            "filledSize", "symbol"
        },
        "min_rows": 1
    },
    "telemetry.csv": {
        "required_columns": {
            "timestamp", "event_type", "order_id", "symbol", "side"
        },
        "min_rows": 1
    },
    "risk_snapshots.csv": {
        "required_columns": {
            "timestamp", "symbol", "position_size", "unrealized_pnl", "margin_used"
        },
        "min_rows": 1300
    },
    "trade_closes.log": {
        "required_columns": set(),  # Log file, no CSV columns
        "min_rows": 1
    },
    "run_metadata.json": {
        "required_columns": set(),  # JSON file
        "min_rows": 0
    },
    "closed_trades_fifo_reconstructed.csv": {
        "required_columns": {
            "timestamp", "order_id", "symbol", "position_side", "qty",
            "open_time", "close_time", "pnl_currency", "mae_pips", "mfe_pips"
        },
        "min_rows": 1
    }
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Validate Gate24h run artifacts")
    parser.add_argument("--dir", required=True, help="Directory containing run artifacts")
    parser.add_argument("--output", help="Output JSON summary file")
    return parser.parse_args()


def validate_csv_file(file_path: Path, required_columns: Set[str], min_rows: int) -> Dict[str, Any]:
    """Validate a CSV file for required columns and minimum row count."""
    result = {
        "exists": file_path.exists(),
        "columns_valid": False,
        "row_count": 0,
        "missing_columns": [],
        "errors": []
    }
    
    if not result["exists"]:
        result["errors"].append(f"File does not exist: {file_path}")
        return result
    
    try:
        with open(file_path, 'r', encoding='utf-8-sig', newline='') as f:
            reader = csv.DictReader(f)
            fieldnames = set(reader.fieldnames or [])
            
            missing = required_columns - fieldnames
            result["missing_columns"] = list(missing)
            result["columns_valid"] = len(missing) == 0
            
            # Count rows
            for _ in reader:
                result["row_count"] += 1
            
            if result["row_count"] < min_rows:
                result["errors"].append(f"Insufficient rows: {result['row_count']} < {min_rows}")
                
    except Exception as e:
        result["errors"].append(f"Failed to read CSV: {str(e)}")
    
    return result


def validate_log_file(file_path: Path, min_rows: int) -> Dict[str, Any]:
    """Validate a log file for minimum line count."""
    result = {
        "exists": file_path.exists(),
        "row_count": 0,
        "errors": []
    }
    
    if not result["exists"]:
        result["errors"].append(f"File does not exist: {file_path}")
        return result
    
    try:
        with open(file_path, 'r', encoding='utf-8', errors='replace') as f:
            result["row_count"] = sum(1 for _ in f)
        
        if result["row_count"] < min_rows:
            result["errors"].append(f"Insufficient lines: {result['row_count']} < {min_rows}")
            
    except Exception as e:
        result["errors"].append(f"Failed to read log file: {str(e)}")
    
    return result


def validate_json_file(file_path: Path) -> Dict[str, Any]:
    """Validate JSON metadata file."""
    result = {
        "exists": file_path.exists(),
        "valid_json": False,
        "mode_valid": False,
        "simulation_disabled": False,
        "content": {},
        "errors": []
    }
    
    if not result["exists"]:
        result["errors"].append(f"File does not exist: {file_path}")
        return result
    
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            content = json.load(f)
        
        result["valid_json"] = True
        result["content"] = content
        
        # Check mode is paper
        mode = content.get("mode", "").lower()
        result["mode_valid"] = mode == "paper"
        if not result["mode_valid"]:
            result["errors"].append(f"Invalid mode: '{mode}' (expected 'paper')")
        
        # Check simulation is disabled
        sim_enabled = content.get("simulation", {}).get("enabled", True)
        result["simulation_disabled"] = not sim_enabled
        if sim_enabled:
            result["errors"].append("Simulation should be disabled (simulation.enabled=false)")
            
    except json.JSONDecodeError as e:
        result["errors"].append(f"Invalid JSON: {str(e)}")
    except Exception as e:
        result["errors"].append(f"Failed to read JSON: {str(e)}")
    
    return result


def validate_artifacts(run_dir: Path) -> Dict[str, Any]:
    """Validate all required artifacts in the run directory."""
    validation_results = {}
    overall_valid = True
    
    for filename, spec in REQUIRED_FILES.items():
        file_path = run_dir / filename
        required_columns = spec["required_columns"]
        min_rows = spec["min_rows"]
        
        if filename.endswith('.csv'):
            result = validate_csv_file(file_path, required_columns, min_rows)
        elif filename.endswith('.log'):
            result = validate_log_file(file_path, min_rows)
        elif filename.endswith('.json'):
            result = validate_json_file(file_path)
        else:
            result = {"exists": file_path.exists(), "errors": ["Unknown file type"]}
        
        validation_results[filename] = result
        
        # Check if this file validation passed
        file_valid = (
            result["exists"] and 
            len(result["errors"]) == 0 and
            (not filename.endswith('.csv') or result["columns_valid"])
        )
        
        if not file_valid:
            overall_valid = False
    
    return {
        "overall_valid": overall_valid,
        "run_directory": str(run_dir),
        "files": validation_results,
        "summary": {
            "total_files": len(REQUIRED_FILES),
            "files_present": sum(1 for r in validation_results.values() if r["exists"]),
            "files_valid": sum(1 for filename, r in validation_results.items() 
                             if r["exists"] and len(r["errors"]) == 0 and
                             (not filename.endswith('.csv') or r["columns_valid"]))
        }
    }


def main() -> int:
    args = parse_args()
    run_dir = Path(args.dir)
    
    if not run_dir.exists():
        print(f"ERROR: Run directory does not exist: {run_dir}", file=sys.stderr)
        return 1
    
    print(f"Validating artifacts in: {run_dir}")
    
    results = validate_artifacts(run_dir)
    
    # Output results
    if args.output:
        output_path = Path(args.output)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        with open(output_path, 'w', encoding='utf-8') as f:
            json.dump(results, f, indent=2)
        print(f"Validation results written to: {output_path}")
    
    # Print summary
    summary = results["summary"]
    print(f"\nValidation Summary:")
    print(f"  Files present: {summary['files_present']}/{summary['total_files']}")
    print(f"  Files valid: {summary['files_valid']}/{summary['total_files']}")
    
    # Print detailed errors
    has_errors = False
    for filename, file_result in results["files"].items():
        if not file_result["exists"]:
            print(f"  ❌ {filename}: Missing")
            has_errors = True
        elif file_result["errors"]:
            print(f"  ❌ {filename}: {'; '.join(file_result['errors'])}")
            has_errors = True
        elif filename.endswith('.csv') and not file_result["columns_valid"]:
            missing = file_result["missing_columns"]
            print(f"  ❌ {filename}: Missing columns: {', '.join(missing)}")
            has_errors = True
        else:
            print(f"  ✅ {filename}: Valid ({file_result.get('row_count', 0)} rows)")
    
    if results["overall_valid"]:
        print("\n🎉 All artifacts are valid!")
        return 0
    else:
        print(f"\n❌ Validation failed - {summary['total_files'] - summary['files_valid']} file(s) have issues")
        return 1


if __name__ == "__main__":
    sys.exit(main())