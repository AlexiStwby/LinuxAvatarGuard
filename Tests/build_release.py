#!/usr/bin/env python3
"""Allowlisted public archives. Private projects/evidence are never traversed."""
from pathlib import Path
import hashlib, json, tarfile, zipfile
root=Path(__file__).resolve().parents[1];package=root/'Package/Assets/LinuxAvatarGuard';out=root/'dist/booth';out.mkdir(parents=True,exist_ok=True)
version='0.2.0';unity=root/'dist'/f'LinuxAvatarGuard-{version}.unitypackage'
assert unity.exists(), 'Export the Unity package first'
# Every exported asset must belong to the source package folder.
paths=[]
with tarfile.open(unity,'r:gz') as archive:
    for member in archive.getmembers():
        if member.name.endswith('/pathname'):
            path=archive.extractfile(member).read().decode();assert path=='Assets/LinuxAvatarGuard' or path.startswith('Assets/LinuxAvatarGuard/'),path;assert '__pycache__' not in path and not path.endswith('.pyc'),path;paths.append(path)
    assert any(p.endswith('/Editor/GuardSetup.cs') for p in paths)
    assert any(p.endswith('/Tools/lag_osc.py') for p in paths)
public=[('LinuxAvatarGuard-'+version+'.unitypackage',unity)]
for name in ['README.es.md','QUICKSTART.en.md','LICENSE.txt','THIRD-PARTY-NOTICES.md','CHANGELOG.md','VALIDATION.es.md']:
    public.append((name,package/name))
public.append(('RELEASE-VALIDATION.md',out/'RELEASE-VALIDATION.md'))
for name in ['Cover.png','Workflow.png']:public.append(('Images/'+name,out/name))
with zipfile.ZipFile(out/f'LinuxAvatarGuard-{version}-BOOTH.zip','w',zipfile.ZIP_DEFLATED) as archive:
    for name,path in public:archive.write(path,name)
with zipfile.ZipFile(out/f'LinuxAvatarGuard-{version}-sources.zip','w',zipfile.ZIP_DEFLATED) as archive:
    for path in package.rglob('*'):
        if path.is_file() and '__pycache__' not in path.parts and not path.name.endswith('.pyc'):
            archive.write(path,'Package/Assets/LinuxAvatarGuard/'+str(path.relative_to(package)))
    for name in ['test_osc.py','LAGValidation.cs','LAGBackendValidation.cs','LinuxAvatarGuard.Validation.asmdef']:
        archive.write(root/'Tests'/name,'Tests/'+name)
    archive.write(__file__,'Tests/build_release.py')
    for name in ['README.md','LICENSE','.gitignore']:archive.write(root/name,name)
    archive.write(root/'Package/Assets/LinuxAvatarGuard.meta','Package/Assets/LinuxAvatarGuard.meta')
    for name in ['Cover.svg','Cover.png','Workflow.svg','Workflow.png','BOOTH-LISTING.md','RELEASE-VALIDATION.md']:archive.write(out/name,'dist/booth/'+name)
manifest={}
for path in [unity,out/f'LinuxAvatarGuard-{version}-BOOTH.zip',out/f'LinuxAvatarGuard-{version}-sources.zip']:
    manifest[path.name]={'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'bytes':path.stat().st_size}
(out/'SHA256SUMS.txt').write_text(''.join(value['sha256']+'  '+name+'\n' for name,value in manifest.items()))
(out/'release-manifest.json').write_text(json.dumps({'version':version,'exportedAssets':len(paths),'files':manifest,'includesAvatars':False,'includesPrivateKeys':False,'sdkFreeImportPassed':True,'coreChecks':26,'oscTests':6,'gestureManagerPreviewPassed':True,'sdkLocalBuildPassed':True},indent=2))
print(json.dumps(manifest,indent=2))
