#!/usr/bin/env python3
"""
Agent A - Telemetry Analyzer (MVP)

Reads orders.csv + risk_snapshots.csv → generates report.pdf + kpi.json

Usage:
    python scripts/postrun_report.py --orders <path> --risk <path> --out <outdir>

Features:
    - Equity curve plot
    - P&L analysis (total, max DD, max profit)
    - Trade statistics (count, win rate, avg P&L)
    - R-violations detection
    - Fill rate & slippage analysis
    - Outputs: report.pdf + kpi.json
"""

import argparse
import json
import sys
from pathlib import Path
from datetime import datetime
from typing import Dict, List, Tuple, Optional

try:
    import pandas as pd
    import matplotlib
    matplotlib.use('Agg')  # Non-interactive backend
    import matplotlib.pyplot as plt
    from matplotlib.backends.backend_pdf import PdfPages
    import matplotlib.dates as mdates
except ImportError as e:
    print(f"ERROR: Missing required package: {e}")
    print("\nInstall required packages:")
    print("  pip install pandas matplotlib")
    sys.exit(1)


class TelemetryAnalyzer:
    """Analyzes trading telemetry and generates reports"""
    
    def __init__(self, orders_path: Path, risk_path: Path):
        self.orders_path = orders_path
        self.risk_path = risk_path
        self.orders_df = None
        self.risk_df = None
        self.kpi = {}
        
    def load_data(self) -> bool:
        """Load CSV files"""
        try:
            print(f"Loading orders from: {self.orders_path}")
            # Use python engine for large files
            self.orders_df = pd.read_csv(self.orders_path, engine='python', on_bad_lines='skip')
            print(f"  → {len(self.orders_df)} orders loaded")
            
            print(f"Loading risk snapshots from: {self.risk_path}")
            self.risk_df = pd.read_csv(self.risk_path, engine='python', on_bad_lines='skip')
            print(f"  → {len(self.risk_df)} snapshots loaded")
            
            # Parse timestamps
            if 'timestamp_utc' in self.risk_df.columns:
                self.risk_df['timestamp'] = pd.to_datetime(self.risk_df['timestamp_utc'])
            
            return True
        except Exception as e:
            print(f"ERROR loading data: {e}")
            return False
    
    def analyze_equity(self) -> Dict:
        """Analyze equity curve"""
        if self.risk_df is None or len(self.risk_df) == 0:
            return {}
        
        equity = self.risk_df['equity'].astype(float)
        balance = self.risk_df['balance'].astype(float)
        
        initial_equity = equity.iloc[0]
        final_equity = equity.iloc[-1]
        max_equity = equity.max()
        min_equity = equity.min()
        
        # Drawdown calculation
        running_max = equity.expanding().max()
        drawdown = equity - running_max
        max_drawdown = drawdown.min()
        max_drawdown_pct = (max_drawdown / running_max.max() * 100) if running_max.max() > 0 else 0
        
        return {
            'initial_equity': float(initial_equity),
            'final_equity': float(final_equity),
            'total_pnl': float(final_equity - initial_equity),
            'total_pnl_pct': float((final_equity - initial_equity) / initial_equity * 100) if initial_equity > 0 else 0,
            'max_equity': float(max_equity),
            'min_equity': float(min_equity),
            'max_drawdown': float(max_drawdown),
            'max_drawdown_pct': float(max_drawdown_pct),
        }
    
    def analyze_trades(self) -> Dict:
        """Analyze closed trades from orders"""
        if self.orders_df is None or len(self.orders_df) == 0:
            return {'total_trades': 0}
        
        # Normalize status column (case-insensitive)
        df = self.orders_df.copy()
        df['status_upper'] = df['status'].str.upper()
        
        # Count by phase (REQUEST/ACK/FILL)
        phase_counts = df['status_upper'].value_counts().to_dict()
        
        # Filter filled orders (case-insensitive: FILL|Filled|fill)
        filled = df[df['status_upper'].isin(['FILL', 'FILLED'])]
        
        # Per-side statistics (if 'side' or 'action' column exists)
        side_stats = {}
        side_col = None
        if 'side' in df.columns:
            side_col = 'side'
        elif 'action' in df.columns:
            side_col = 'action'
        
        if side_col:
            # REQUEST/ACK/FILL per side
            for side in df[side_col].unique():
                if pd.isna(side):
                    continue
                side_df = df[df[side_col] == side]
                side_stats[f'{side}_REQUEST'] = int((side_df['status_upper'] == 'REQUEST').sum())
                side_stats[f'{side}_ACK'] = int((side_df['status_upper'] == 'ACK').sum())
                side_stats[f'{side}_FILL'] = int(side_df['status_upper'].isin(['FILL', 'FILLED']).sum())
        
        # Calculate fill-rate properly:
        # Fill-rate = (#unique order_id with FILL) / (#unique order_id with REQUEST)
        order_id_col = None
        for col in ['order_id', 'orderId', 'client_order_id']:
            if col in df.columns:
                order_id_col = col
                break
        
        fill_rate = 0.0
        unique_fills = 0
        unique_requests = 0
        
        if order_id_col:
            request_orders = df[df['status_upper'] == 'REQUEST'][order_id_col].nunique()
            fill_orders = df[df['status_upper'].isin(['FILL', 'FILLED'])][order_id_col].nunique()
            unique_requests = request_orders
            unique_fills = fill_orders
            fill_rate = (fill_orders / request_orders * 100) if request_orders > 0 else 0
        else:
            # Fallback: count rows
            total_requests = (df['status_upper'] == 'REQUEST').sum()
            total_fills = df['status_upper'].isin(['FILL', 'FILLED']).sum()
            unique_requests = int(total_requests)
            unique_fills = int(total_fills)
            fill_rate = (total_fills / total_requests * 100) if total_requests > 0 else 0
        
        # Slippage analysis (if available)
        slippage_data = {}
        if len(filled) > 0:
            # Try different column names for slippage
            req_price_col = None
            fill_price_col = None
            
            for col in ['requested_price', 'price_requested', 'intendedPrice']:
                if col in filled.columns:
                    req_price_col = col
                    break
            
            for col in ['fill_price', 'price_filled', 'execPrice']:
                if col in filled.columns:
                    fill_price_col = col
                    break
            
            if req_price_col and fill_price_col:
                filled_copy = filled.copy()
                filled_copy['slippage'] = filled_copy[fill_price_col].astype(float) - filled_copy[req_price_col].astype(float)
                avg_slippage = filled_copy['slippage'].mean()
                max_slippage = filled_copy['slippage'].abs().max()
                slippage_data = {
                    'avg_slippage': float(avg_slippage) if pd.notna(avg_slippage) else 0,
                    'max_slippage': float(max_slippage) if pd.notna(max_slippage) else 0,
                }
        
        return {
            'total_filled_orders': int(len(filled)),
            'unique_filled_orders': int(unique_fills),
            'unique_requested_orders': int(unique_requests),
            'total_rows': int(len(df)),
            'fill_rate_pct': float(fill_rate),
            'phase_counts': phase_counts,
            **side_stats,
            **slippage_data
        }
    
    def analyze_risk(self) -> Dict:
        """Analyze risk metrics from risk_snapshots"""
        if self.risk_df is None or len(self.risk_df) == 0:
            return {}
        
        # Check for open_pnl and closed_pnl columns (Agent A)
        has_open_pnl = 'open_pnl' in self.risk_df.columns
        has_closed_pnl = 'closed_pnl' in self.risk_df.columns
        
        risk_data = {
            'has_open_pnl_column': has_open_pnl,
            'has_closed_pnl_column': has_closed_pnl,
        }
        
        if has_closed_pnl:
            closed_pnl = self.risk_df['closed_pnl'].astype(float)
            risk_data['final_closed_pnl'] = float(closed_pnl.iloc[-1])
            risk_data['closed_pnl_monotonic'] = bool(closed_pnl.is_monotonic_increasing)
        
        if has_open_pnl:
            open_pnl = self.risk_df['open_pnl'].astype(float)
            risk_data['final_open_pnl'] = float(open_pnl.iloc[-1])
        
        # R-usage violations (if R_used column exists)
        if 'R_used' in self.risk_df.columns:
            r_used = self.risk_df['R_used'].astype(float)
            r_violations = (r_used > 1.0).sum()
            max_r_used = r_used.max()
            risk_data['r_violations_count'] = int(r_violations)
            risk_data['max_r_used'] = float(max_r_used)
        
        # Margin usage
        if 'margin' in self.risk_df.columns and 'free_margin' in self.risk_df.columns:
            margin = self.risk_df['margin'].astype(float)
            free_margin = self.risk_df['free_margin'].astype(float)
            total_margin = margin + free_margin
            margin_usage_pct = (margin / total_margin * 100).where(total_margin > 0, 0)
            risk_data['max_margin_usage_pct'] = float(margin_usage_pct.max())
            risk_data['avg_margin_usage_pct'] = float(margin_usage_pct.mean())
        
        return risk_data
    
    def compute_kpi(self) -> Dict:
        """Compute all KPIs"""
        equity_kpi = self.analyze_equity()
        trade_kpi = self.analyze_trades()
        risk_kpi = self.analyze_risk()
        
        # Metadata
        metadata = {
            'generated_at': datetime.utcnow().isoformat() + 'Z',
            'orders_file': str(self.orders_path.name),
            'risk_file': str(self.risk_path.name),
            'orders_count': len(self.orders_df) if self.orders_df is not None else 0,
            'snapshots_count': len(self.risk_df) if self.risk_df is not None else 0,
        }
        
        return {
            'metadata': metadata,
            'equity': equity_kpi,
            'trades': trade_kpi,
            'risk': risk_kpi,
        }
    
    def plot_equity_curve(self, ax):
        """Plot equity curve"""
        if self.risk_df is None or len(self.risk_df) == 0:
            ax.text(0.5, 0.5, 'No data', ha='center', va='center')
            return
        
        timestamps = self.risk_df['timestamp'] if 'timestamp' in self.risk_df.columns else range(len(self.risk_df))
        equity = self.risk_df['equity'].astype(float)
        balance = self.risk_df['balance'].astype(float)
        
        ax.plot(timestamps, equity, label='Equity', linewidth=2, color='#2E86AB')
        ax.plot(timestamps, balance, label='Balance', linewidth=1.5, color='#A23B72', linestyle='--')
        
        ax.set_xlabel('Time', fontsize=10)
        ax.set_ylabel('Value (USD)', fontsize=10)
        ax.set_title('Equity Curve', fontsize=12, fontweight='bold')
        ax.legend(loc='best', fontsize=9)
        ax.grid(True, alpha=0.3)
        
        if 'timestamp' in self.risk_df.columns:
            ax.xaxis.set_major_formatter(mdates.DateFormatter('%m-%d %H:%M'))
            plt.setp(ax.xaxis.get_majorticklabels(), rotation=45, ha='right')
    
    def plot_pnl_breakdown(self, ax):
        """Plot P&L breakdown (open vs closed)"""
        if self.risk_df is None or 'open_pnl' not in self.risk_df.columns:
            ax.text(0.5, 0.5, 'No P&L data', ha='center', va='center')
            return
        
        timestamps = self.risk_df['timestamp'] if 'timestamp' in self.risk_df.columns else range(len(self.risk_df))
        open_pnl = self.risk_df['open_pnl'].astype(float)
        closed_pnl = self.risk_df['closed_pnl'].astype(float) if 'closed_pnl' in self.risk_df.columns else pd.Series([0] * len(self.risk_df))
        
        ax.plot(timestamps, closed_pnl, label='Closed P&L', linewidth=2, color='#06A77D')
        ax.plot(timestamps, open_pnl, label='Open P&L', linewidth=1.5, color='#F77F00', alpha=0.7)
        
        ax.set_xlabel('Time', fontsize=10)
        ax.set_ylabel('P&L (USD)', fontsize=10)
        ax.set_title('P&L Breakdown (Agent A)', fontsize=12, fontweight='bold')
        ax.legend(loc='best', fontsize=9)
        ax.grid(True, alpha=0.3)
        ax.axhline(y=0, color='black', linestyle='-', linewidth=0.5, alpha=0.5)
        
        if 'timestamp' in self.risk_df.columns:
            ax.xaxis.set_major_formatter(mdates.DateFormatter('%m-%d %H:%M'))
            plt.setp(ax.xaxis.get_majorticklabels(), rotation=45, ha='right')
    
    def plot_drawdown(self, ax):
        """Plot drawdown curve"""
        if self.risk_df is None or len(self.risk_df) == 0:
            ax.text(0.5, 0.5, 'No data', ha='center', va='center')
            return
        
        timestamps = self.risk_df['timestamp'] if 'timestamp' in self.risk_df.columns else range(len(self.risk_df))
        equity = self.risk_df['equity'].astype(float)
        
        running_max = equity.expanding().max()
        drawdown = equity - running_max
        drawdown_pct = (drawdown / running_max * 100).where(running_max > 0, 0)
        
        ax.fill_between(timestamps, drawdown_pct, 0, alpha=0.3, color='#D62828')
        ax.plot(timestamps, drawdown_pct, linewidth=1.5, color='#D62828')
        
        ax.set_xlabel('Time', fontsize=10)
        ax.set_ylabel('Drawdown (%)', fontsize=10)
        ax.set_title('Drawdown', fontsize=12, fontweight='bold')
        ax.grid(True, alpha=0.3)
        
        if 'timestamp' in self.risk_df.columns:
            ax.xaxis.set_major_formatter(mdates.DateFormatter('%m-%d %H:%M'))
            plt.setp(ax.xaxis.get_majorticklabels(), rotation=45, ha='right')
    
    def plot_kpi_summary(self, ax):
        """Plot KPI summary as text table"""
        ax.axis('off')
        
        kpi = self.kpi
        
        # Build table data
        table_data = [
            ['Metric', 'Value'],
            ['─' * 30, '─' * 20],
        ]
        
        # Equity metrics
        if 'equity' in kpi and kpi['equity']:
            eq = kpi['equity']
            table_data.append(['Initial Equity', f"${eq.get('initial_equity', 0):,.2f}"])
            table_data.append(['Final Equity', f"${eq.get('final_equity', 0):,.2f}"])
            table_data.append(['Total P&L', f"${eq.get('total_pnl', 0):,.2f} ({eq.get('total_pnl_pct', 0):.2f}%)"])
            table_data.append(['Max Drawdown', f"${eq.get('max_drawdown', 0):,.2f} ({eq.get('max_drawdown_pct', 0):.2f}%)"])
            table_data.append(['─' * 30, '─' * 20])
        
        # Trade metrics
        if 'trades' in kpi and kpi['trades']:
            tr = kpi['trades']
            table_data.append(['Total Orders', f"{tr.get('total_orders', 0):,}"])
            table_data.append(['Fill Rate', f"{tr.get('fill_rate_pct', 0):.2f}%"])
            if 'avg_slippage' in tr:
                table_data.append(['Avg Slippage', f"{tr.get('avg_slippage', 0):.4f}"])
            table_data.append(['─' * 30, '─' * 20])
        
        # Risk metrics (Agent A)
        if 'risk' in kpi and kpi['risk']:
            rk = kpi['risk']
            if rk.get('has_closed_pnl_column'):
                table_data.append(['Final Closed P&L', f"${rk.get('final_closed_pnl', 0):,.2f}"])
            if rk.get('has_open_pnl_column'):
                table_data.append(['Final Open P&L', f"${rk.get('final_open_pnl', 0):,.2f}"])
            if 'r_violations_count' in rk:
                table_data.append(['R Violations', f"{rk.get('r_violations_count', 0):,}"])
            if 'max_margin_usage_pct' in rk:
                table_data.append(['Max Margin Usage', f"{rk.get('max_margin_usage_pct', 0):.2f}%"])
        
        # Render as table
        table = ax.table(cellText=table_data, loc='center', cellLoc='left',
                        colWidths=[0.6, 0.4], bbox=[0, 0, 1, 1])
        table.auto_set_font_size(False)
        table.set_fontsize(9)
        
        # Style header
        for i in range(2):
            for j in range(2):
                cell = table[(i, j)]
                cell.set_facecolor('#E8E8E8')
                cell.set_text_props(weight='bold')
        
        ax.set_title('KPI Summary', fontsize=12, fontweight='bold', pad=20)
    
    def generate_pdf_report(self, output_path: Path):
        """Generate PDF report"""
        print(f"\nGenerating PDF report: {output_path}")
        
        with PdfPages(output_path) as pdf:
            # Page 1: Overview
            fig, axes = plt.subplots(2, 2, figsize=(11, 8.5))
            fig.suptitle('Agent A - Telemetry Report', fontsize=16, fontweight='bold')
            
            self.plot_equity_curve(axes[0, 0])
            self.plot_pnl_breakdown(axes[0, 1])
            self.plot_drawdown(axes[1, 0])
            self.plot_kpi_summary(axes[1, 1])
            
            plt.tight_layout()
            pdf.savefig(fig, bbox_inches='tight')
            plt.close(fig)
            
            print("  ✓ Page 1: Overview (equity, P&L, drawdown, KPI)")
        
        print(f"  → PDF saved: {output_path}")
    
    def save_kpi_json(self, output_path: Path):
        """Save KPI as JSON"""
        print(f"\nSaving KPI JSON: {output_path}")
        
        with open(output_path, 'w', encoding='utf-8') as f:
            json.dump(self.kpi, f, indent=2)
        
        print(f"  → JSON saved: {output_path}")
    
    def run(self, output_dir: Path):
        """Run full analysis"""
        print("\n" + "="*70)
        print("  AGENT A - TELEMETRY ANALYZER (MVP)")
        print("="*70)
        
        # Load data
        if not self.load_data():
            return False
        
        # Compute KPI
        print("\nComputing KPI...")
        self.kpi = self.compute_kpi()
        print("  ✓ KPI computed")
        
        # Generate outputs
        output_dir.mkdir(parents=True, exist_ok=True)
        
        pdf_path = output_dir / 'report.pdf'
        json_path = output_dir / 'kpi.json'
        
        self.generate_pdf_report(pdf_path)
        self.save_kpi_json(json_path)
        
        print("\n" + "="*70)
        print("  ✅ ANALYSIS COMPLETE")
        print("="*70)
        print(f"\nOutputs:")
        print(f"  - {pdf_path}")
        print(f"  - {json_path}")
        print()
        
        return True


def main():
    parser = argparse.ArgumentParser(
        description='Agent A - Telemetry Analyzer (MVP)',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog='''
Examples:
  python scripts/postrun_report.py --orders orders.csv --risk risk_snapshots.csv --out reports/
  python scripts/postrun_report.py --orders D:/botg/logs/orders.csv --risk D:/botg/logs/risk_snapshots.csv --out D:/botg/reports/
        '''
    )
    
    parser.add_argument('--orders', required=True, type=Path,
                       help='Path to orders.csv')
    parser.add_argument('--risk', required=True, type=Path,
                       help='Path to risk_snapshots.csv')
    parser.add_argument('--out', required=True, type=Path,
                       help='Output directory for report.pdf and kpi.json')
    
    args = parser.parse_args()
    
    # Validate inputs
    if not args.orders.exists():
        print(f"ERROR: Orders file not found: {args.orders}")
        sys.exit(1)
    
    if not args.risk.exists():
        print(f"ERROR: Risk snapshots file not found: {args.risk}")
        sys.exit(1)
    
    # Run analyzer
    analyzer = TelemetryAnalyzer(args.orders, args.risk)
    success = analyzer.run(args.out)
    
    sys.exit(0 if success else 1)


if __name__ == '__main__':
    main()
