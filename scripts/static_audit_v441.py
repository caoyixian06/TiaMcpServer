from pathlib import Path
import re, sys, json
root=Path(__file__).resolve().parents[1]
errors=[]
def read(rel): return (root/rel).read_text(encoding='utf-8-sig')
server=read('McpServer.cs'); project=read('TiaMcpServer.csproj')
risk=read('Runtime/ToolRiskPolicy.cs'); workflow=read('Services/EngineeringWorkflowService.cs')
svc=read('Services/XmlOrchestrationService.cs'); tools=read('Tools/XmlOrchestrationTools.cs')
builder=read('Xml/HmiScreenXmlBuilder.cs'); setup=read('Services/HmiSetupService.cs')
if 'Version = "4.4.1"' not in server: errors.append('McpServer version mismatch')
for value in ['<Version>4.4.1</Version>','<AssemblyVersion>4.4.1.0</AssemblyVersion>','<FileVersion>4.4.1.0</FileVersion>','<InformationalVersion>4.4.1-hmi-ir-import</InformationalVersion>']:
    if value not in project: errors.append('csproj missing '+value)
alltools=['get_xml_ir_capabilities','compile_lad_ir','apply_lad_ir','compile_hmi_ir','validate_hmi_ir','apply_hmi_ir']
for t in alltools:
    if f'RegisterTool("{t}"' not in tools: errors.append('tool not registered: '+t)
for t in ['get_xml_ir_capabilities','compile_lad_ir','compile_hmi_ir','validate_hmi_ir']:
    if t not in risk: errors.append('read-only risk coverage missing: '+t)
if '"apply_"' not in risk: errors.append('apply tools mutation risk prefix missing')
if 'apply_lad_ir' not in workflow: errors.append('workflow stage coverage missing: apply_lad_ir')
for anchor in ['ValidateHmiIr','ApplyHmiIr','ToLegacyHmiItem','multiAction','CreateHmiScreenFromSpec']:
    if anchor not in svc: errors.append('HMI service anchor missing: '+anchor)
for anchor in ['CreateButtonAdvanced','GroupBy','FunctionListEntries','HmiButtonActionSpec']:
    if anchor not in builder: errors.append('builder anchor missing: '+anchor)
if 'CreateButtonAdvanced' not in setup or 'multiAction' not in setup: errors.append('HmiSetupService multiAction integration missing')
for rel in ['Services/XmlOrchestrationService.cs','Tools/XmlOrchestrationTools.cs','Xml/HmiScreenXmlBuilder.cs','Services/HmiSetupService.cs']:
    text=read(rel)
    if text.count('{') != text.count('}'): errors.append(rel+' brace imbalance')
for rel in ['tools_filter.json']:
    json.loads(read(rel))
print('STATIC AUDIT V4.4.1')
print('xml_ir_tools='+str(len(alltools)))
print('instruction_specs='+str(len(re.findall(r'\["[A-Z_]+"\]\s*=\s*Spec\(',svc))))
print('hmi_multi_action=true')
print('errors='+str(len(errors)))
for e in errors: print('ERROR:',e)
sys.exit(1 if errors else 0)
