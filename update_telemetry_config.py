with open('BotG/Telemetry/TelemetryConfig.cs.bak', 'r', encoding='utf-8') as f:
    lines = f.readlines()

output = []
i = 0
while i < len(lines):
    line = lines[i]
    output.append(line)
    
    # After "public ExecutionConfig Execution" line, add new properties
    if 'public ExecutionConfig Execution' in line and 'new ExecutionConfig()' in line:
        output.append('    public FeesConfig Fees { get; set; } = new FeesConfig();\n')
        output.append('    public SpreadConfig Spread { get; set; } = new SpreadConfig();\n')
        output.append('    public SlippageConfig Slippage { get; set; } = new SlippageConfig();\n')
        output.append('    public string MarketSource { get; set; } = "live_feed";\n')
    
    i += 1

# Remove last line if it's just }
if output[-1].strip() == '}':
    output.pop()

# Add new classes
output.append('\n')
output.append('    public class FeesConfig\n')
output.append('    {\n')
output.append('        public double CommissionPerLot { get; set; } = 7.0;\n')
output.append('    }\n')
output.append('\n')
output.append('    public class SpreadConfig\n')
output.append('    {\n')
output.append('        public double PipsBase { get; set; } = 0.1;\n')
output.append('    }\n')
output.append('\n')
output.append('    public class SlippageConfig\n')
output.append('    {\n')
output.append('        public string Mode { get; set; } = "random";\n')
output.append('        public double RangePips { get; set; } = 0.1;\n')
output.append('        public int Seed { get; set; } = 42;\n')
output.append('    }\n')
output.append('}\n')

with open('BotG/Telemetry/TelemetryConfig.cs', 'w', encoding='utf-8') as f:
    f.writelines(output)

print('Successfully updated TelemetryConfig.cs')
