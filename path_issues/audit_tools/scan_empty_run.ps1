param([string]$dir=".github/workflows")
$files = Get-ChildItem $dir -Filter *.yml
foreach($f in $files){
  $lines = Get-Content $f.FullName
  for($i=0; $i -lt $lines.Count - 1; $i++){
    if($lines[$i] -match '^\s+run:\s*\|?\s*$'){
      # Ignore defaults.run: blocks
      if(($i -ge 1) -and $lines[$i-1] -match '^\s*defaults\s*:\s*$'){ continue }
      $indent = ($lines[$i] -replace '^(\s*).*','$1').Length
      $next   = $lines[$i+1]
      $nextIndent = if($next -match '^\s+'){($next -replace '^(\s*).*','$1').Length}else{0}
      if($nextIndent -le $indent -or $next -match '^\s*$'){
        "{0}:{1}: TRUE_EMPTY_RUN_BLOCK" -f $f.Name,($i+1)
      }
    }
  }
}
