#!/usr/bin/env python3
from pathlib import Path
import json, re, sys, xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
errors=[]
notes=[]

# JSON/XML validity
for p in ROOT.rglob('*.json'):
    if 'bin' in p.parts or 'obj' in p.parts: continue
    try: json.loads(p.read_text(encoding='utf-8-sig'))
    except Exception as e: errors.append(f'JSON invalid: {p.relative_to(ROOT)}: {e}')
try: ET.parse(ROOT/'TiaMcpServer.csproj')
except Exception as e: errors.append(f'csproj invalid: {e}')

# Lightweight C# lexical delimiter check (comments and literals ignored).
def strip_cs(text:str)->str:
    out=[]; i=0; n=len(text); state='code'
    while i<n:
        c=text[i]; d=text[i+1] if i+1<n else ''
        if state=='code':
            if c=='/' and d=='/': state='line'; out.extend('  '); i+=2; continue
            if c=='/' and d=='*': state='block'; out.extend('  '); i+=2; continue
            if c=='@' and d=='"': state='vstr'; out.extend('  '); i+=2; continue
            if c=='$' and d=='"': state='str'; out.extend('  '); i+=2; continue
            if c=='$' and d=='@' and i+2<n and text[i+2]=='"': state='vstr'; out.extend('   '); i+=3; continue
            if c=='@' and d=='$' and i+2<n and text[i+2]=='"': state='vstr'; out.extend('   '); i+=3; continue
            if c=='"': state='str'; out.append(' '); i+=1; continue
            if c=="'": state='char'; out.append(' '); i+=1; continue
            out.append(c); i+=1; continue
        if state=='line':
            if c=='\n': state='code'; out.append('\n')
            else: out.append(' ')
            i+=1; continue
        if state=='block':
            if c=='*' and d=='/': state='code'; out.extend('  '); i+=2
            else: out.append('\n' if c=='\n' else ' '); i+=1
            continue
        if state=='str':
            if c=='\\': out.extend('  ' if i+1<n else ' '); i+=2; continue
            if c=='"': state='code'
            out.append('\n' if c=='\n' else ' '); i+=1; continue
        if state=='vstr':
            if c=='"' and d=='"': out.extend('  '); i+=2; continue
            if c=='"': state='code'
            out.append('\n' if c=='\n' else ' '); i+=1; continue
        if state=='char':
            if c=='\\': out.extend('  ' if i+1<n else ' '); i+=2; continue
            if c=="'": state='code'
            out.append('\n' if c=='\n' else ' '); i+=1; continue
    if state in ('block','str','vstr','char'):
        raise ValueError(f'unterminated lexical state {state}')
    return ''.join(out)

pairs={')':'(',']':'[','}':'{'}
for p in ROOT.rglob('*.cs'):
    if 'bin' in p.parts or 'obj' in p.parts: continue
    try: clean=strip_cs(p.read_text(encoding='utf-8-sig'))
    except Exception as e:
        errors.append(f'lexical error: {p.relative_to(ROOT)}: {e}'); continue
    stack=[]
    line=1
    for ch in clean:
        if ch=='\n': line+=1
        elif ch in '([{': stack.append((ch,line))
        elif ch in ')]}':
            if not stack or stack[-1][0] != pairs[ch]:
                errors.append(f'delimiter mismatch: {p.relative_to(ROOT)} line {line}: {ch}')
                break
            stack.pop()
    else:
        if stack: errors.append(f'unclosed delimiter: {p.relative_to(ROOT)} line {stack[-1][1]}: {stack[-1][0]}')

# Version consistency
csproj=(ROOT/'TiaMcpServer.csproj').read_text(encoding='utf-8')
server=(ROOT/'McpServer.cs').read_text(encoding='utf-8')
for expected in ['<Version>4.3.0</Version>','<AssemblyVersion>4.3.0.0</AssemblyVersion>','<FileVersion>4.3.0.0</FileVersion>','<InformationalVersion>4.3.0-autonomous-engineering</InformationalVersion>']:
    if expected not in csproj: errors.append('version missing: '+expected)
if 'Version = "4.3.0"' not in server: errors.append('McpServer version is not 4.3.0')

# Read risk arrays directly from policy source and classify every statically registered tool.
policy=(ROOT/'Runtime'/'ToolRiskPolicy.cs').read_text(encoding='utf-8')
def get_array(name):
    m=re.search(rf'{name}\s*=\s*\{{(.*?)\}};', policy, re.S)
    if not m: raise RuntimeError('risk array not found: '+name)
    return re.findall(r'"([^"]+)"', m.group(1))
arrays={n:get_array(n) for n in ['ReadOnlyPrefixes','ReadOnlyNames','SessionNames','CriticalNames','CriticalFragments','HighPrefixes','KnownMutationPrefixes','OnlineFragments','SecurityFragments','FileSystemPrefixes','LongRunningPrefixes']}
tool_names=set()
for p in ROOT.rglob('*.cs'):
    if 'bin' in p.parts or 'obj' in p.parts: continue
    text=p.read_text(encoding='utf-8-sig')
    tool_names.update(re.findall(r'RegisterTool\(\s*"([^"]+)"', text))
tool_names.update(re.findall(r'_riskPolicy\.Describe\(\s*"([^"]+)"', server))

def classify(name):
    n=name.lower()
    read=any(n.startswith(x) for x in arrays['ReadOnlyPrefixes']) or n in arrays['ReadOnlyNames']
    sess=n in arrays['SessionNames']
    destr=n.startswith(('delete_','remove_','reset_')) or '_delete_' in n or 'factory_reset' in n
    known=any(n.startswith(x) for x in arrays['KnownMutationPrefixes']) or destr
    risk='Unknown'
    if read: risk='Low'
    elif sess: risk='Medium'
    elif known: risk='Medium'
    if any(n.startswith(x) for x in arrays['HighPrefixes']) or destr: risk='High'
    if n in arrays['CriticalNames'] or any(x in n for x in arrays['CriticalFragments']): risk='Critical'
    security=any(x in n for x in arrays['SecurityFragments'])
    if security and risk not in ('Unknown','High','Critical'): risk='High'
    filesystem=any(n.startswith(x) for x in arrays['FileSystemPrefixes'])
    if filesystem and risk not in ('Unknown','High','Critical') and not read: risk='High'
    online=any(x in n for x in arrays['OnlineFragments']) or n in arrays['CriticalNames'] or n in ('go_online','go_offline')
    return risk, online
unknown=[n for n in sorted(tool_names) if classify(n)[0]=='Unknown']
if unknown: errors.append(f'{len(unknown)} tools have Unknown risk: '+', '.join(unknown[:20]))
notes.append(f'statically registered tools: {len(tool_names)}; Unknown risk: {len(unknown)}')

# Protected online methods must only be reachable from tool scopes marked online.
for n in ['download_to_device','download_to_device_enhanced','download_to_device_full','download_with_config','write_online_variable','upload_from_device','upload_station','cpu_stop','cpu_start','set_device_ip','set_plc_master_secret_online','reset_plc_master_secret_online','orchestrate_project','create_project_from_template','auto_configure_network','auto_compile_and_download','batch_operation','setup_network_and_hmi_connection']:
    if not classify(n)[1]: errors.append(f'online-capable tool not marked online: {n}')

# UI DPI policy.
forms=list((ROOT/'UI').glob('*Form.cs'))
for p in forms:
    t=p.read_text(encoding='utf-8-sig')
    if 'AutoScaleMode = AutoScaleMode.Dpi' not in t: errors.append(f'DPI mode missing: {p.name}')
notes.append(f'WinForms DPI checked: {len(forms)} forms')

# Security-chain anchors.
anchors={
    'WorkflowGate -> Schema -> RiskCheck -> Permission -> ControlLock -> Audit -> Execute':'WorkflowGate -> Schema -> RiskCheck -> Permission -> ControlLock -> Audit -> Execute' in server,
    'schema validator':'ToolSchemaValidator.TryValidate' in server,
    'read/write lock':'EnterRead(cancellationToken)' in server and 'EnterWrite(cancellationToken)' in server,
    'online scope':'SafeOnlineExecutor.EnterAuthorizedScope' in server,
    'job API':all(x in server for x in ['get_job_status','list_jobs','cancel_job']),
    'delete confirm':all(x in server for x in ['dryRun','confirmation','confirm']),
    'batch whitelist':'仅允许 ToolMethodMap 明确列出的安全白名单' in (ROOT/'Services'/'OrchestratorService.cs').read_text(encoding='utf-8'),
}
for k,v in anchors.items():
    if not v: errors.append('security anchor missing: '+k)


# Engineering workflow anchors and compact standards packs.
workflow_service=(ROOT/'Services'/'EngineeringWorkflowService.cs').read_text(encoding='utf-8-sig')
workflow_tools=(ROOT/'Tools'/'EngineeringWorkflowTools.cs').read_text(encoding='utf-8-sig')
required_workflow_tools=[
    'create_requirement_session','update_requirement_session','freeze_requirements',
    'generate_project_plan','approve_project_plan','get_requirement_status',
    'get_current_workflow_stage','set_active_engineering_session','deactivate_engineering_session',
    'advance_engineering_stage','record_engineering_test_evidence','approve_engineering_acceptance','get_applicable_standards',
    'validate_workflow_readiness','prepare_autonomous_project','generate_engineering_blueprint',
    'get_engineering_blueprint','validate_engineering_blueprint'
]
for name in required_workflow_tools:
    if f'RegisterTool("{name}"' not in workflow_tools: errors.append('workflow tool missing: '+name)
    if name not in policy: errors.append('workflow risk coverage missing: '+name)
for anchor in ['EvaluateBeforeTool','RecordToolSuccess','GetCompactServerInstructions','EngineeringStage','batch_operation']:
    if anchor not in workflow_service and anchor not in server: errors.append('workflow anchor missing: '+anchor)
standards_dir=ROOT/'standards'/'engineering'
expected_packs=['core','project-creation','hardware','plc-architecture','lad','hmi-planning','hmi-implementation','verification','deployment']
for name in expected_packs:
    p=standards_dir/(name+'.json')
    if not p.exists(): errors.append('standards pack missing: '+name)
    else:
        data=json.loads(p.read_text(encoding='utf-8-sig'))
        if not data.get('rules'): errors.append('standards pack has no rules: '+name)
        if len(data.get('rules',[])) > 20: errors.append('standards pack too large: '+name)
if 'standards\\**\\*' not in csproj: errors.append('standards not copied by csproj')
policy_json=json.loads((ROOT/'server_policy.json').read_text(encoding='utf-8-sig'))
if not policy_json.get('engineeringWorkflow',{}).get('enabled'): errors.append('engineeringWorkflow policy is not enabled')
notes.append(f'workflow tools: {len(required_workflow_tools)}; standards packs: {len(expected_packs)}')

auto=(ROOT/'Services'/'AutonomousEngineeringService.cs').read_text(encoding='utf-8-sig')
for needle_auto in ['PrepareAutonomousProject','ApplyAutonomousDefaults','BuildBlueprint','ValidateEngineeringBlueprint']:
    if needle_auto not in auto: errors.append('missing autonomous feature: '+needle_auto)
print('STATIC AUDIT V4.3.0')
for n in notes: print('OK:',n)
for k,v in anchors.items(): print(('OK' if v else 'FAIL')+':',k)
if errors:
    print('\nFAILURES:')
    for e in errors: print('-',e)
    sys.exit(1)
print('\nRESULT: PASS (static checks only; not a compiler or TIA runtime test)')
