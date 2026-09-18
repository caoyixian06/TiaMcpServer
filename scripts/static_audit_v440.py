from pathlib import Path
import re, sys
root=Path(__file__).resolve().parents[1]
errors=[]

def read(rel): return (root/rel).read_text(encoding='utf-8-sig')
server=read('McpServer.cs'); project=read('TiaMcpServer.csproj')
risk=read('Runtime/ToolRiskPolicy.cs'); workflow=read('Services/EngineeringWorkflowService.cs')
svc=read('Services/XmlOrchestrationService.cs'); tools=read('Tools/XmlOrchestrationTools.cs')
if 'Version = "4.4.0"' not in server: errors.append('McpServer version mismatch')
for value in ['<Version>4.4.0</Version>','<AssemblyVersion>4.4.0.0</AssemblyVersion>','<FileVersion>4.4.0.0</FileVersion>','<InformationalVersion>4.4.0-xml-ir-orchestrator</InformationalVersion>']:
    if value not in project: errors.append('csproj missing '+value)
for t in ['get_xml_ir_capabilities','compile_lad_ir','apply_lad_ir','compile_hmi_ir']:
    if f'RegisterTool("{t}"' not in tools: errors.append('tool not registered: '+t)
for t in ['get_xml_ir_capabilities','compile_lad_ir','compile_hmi_ir']:
    if t not in risk: errors.append('read-only risk coverage missing: '+t)
if 'apply_lad_ir' not in workflow: errors.append('workflow stage coverage missing: apply_lad_ir')
for anchor in ['InstructionSpec','CompileLadIr','ConvertStep','CompileHmiIr','pumpFaceplate','AddLadNetworksBatch']:
    if anchor not in svc: errors.append('service anchor missing: '+anchor)
# crude structural checks
for rel in ['Services/XmlOrchestrationService.cs','Tools/XmlOrchestrationTools.cs']:
    s=read(rel)
    if s.count('{') != s.count('}'): errors.append(rel+' brace imbalance')
    if s.count('(') != s.count(')'): errors.append(rel+' parenthesis imbalance')
print('STATIC AUDIT V4.4.0')
print('xml_ir_tools=4')
print('instruction_specs='+str(len(re.findall(r'\["[A-Z_]+"\]\s*=\s*Spec\(',svc))))
print('errors='+str(len(errors)))
for e in errors: print('ERROR:',e)
sys.exit(1 if errors else 0)
