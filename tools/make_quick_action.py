"""Installs the Finder Quick Action "Convert…" (~/Library/Services/Convert.workflow).

It just hands the selected files to Convert.app, so it never needs rebuilding.
"""
import os, plistlib, uuid

SCRIPT = 'open -b local.convert.app "$@"\n'
TYPES = ["public.audio", "public.movie", "public.audiovisual-content", "public.image", "com.adobe.pdf",
         "public.plain-text", "public.rtf", "com.apple.rtfd", "public.html", "com.apple.webarchive",
         "org.openxmlformats.wordprocessingml.document", "com.microsoft.word.doc",
         "org.oasis-open.opendocument.text", "net.daringfireball.markdown"]

dest = os.path.expanduser("~/Library/Services/Convert.workflow/Contents")
os.makedirs(dest, exist_ok=True)
U = lambda: str(uuid.uuid4()).upper()

info = {
    "CFBundleName": "Convert",
    "NSServices": [{
        "NSMenuItem": {"default": "Convert…"},
        "NSMessage": "runWorkflowAsService",
        "NSRequiredContext": {"NSApplicationIdentifier": "com.apple.finder"},
        "NSSendFileTypes": TYPES,
        "NSIconName": "NSActionTemplate",
    }],
}
action = {
    "AMAccepts": {"Container": "List", "Optional": True, "Types": ["com.apple.cocoa.string"]},
    "AMActionVersion": "2.0.3", "AMApplication": ["Automator"],
    "AMParameterProperties": {k: {} for k in ["COMMAND_STRING", "CheckedForUserDefaultShell", "inputMethod", "shell", "source"]},
    "AMProvides": {"Container": "List", "Types": ["com.apple.cocoa.string"]},
    "ActionBundlePath": "/System/Library/Automator/Run Shell Script.action",
    "ActionName": "Run Shell Script",
    "ActionParameters": {"COMMAND_STRING": SCRIPT, "CheckedForUserDefaultShell": True,
                         "inputMethod": 1, "shell": "/bin/zsh", "source": ""},
    "BundleIdentifier": "com.apple.RunShellScript", "CFBundleVersion": "2.0.3",
    "CanShowSelectedItemsWhenRun": False, "CanShowWhenRun": True,
    "Category": ["AMCategoryUtilities"], "Class Name": "RunShellScriptAction",
    "InputUUID": U(), "OutputUUID": U(), "UUID": U(),
    "Keywords": ["Shell", "Script"], "UnlocalizedApplications": ["Automator"], "isViewVisible": 1,
}
doc = {
    "AMApplicationBuild": "523", "AMApplicationVersion": "2.10", "AMDocumentVersion": "2",
    "actions": [{"action": action, "isViewVisible": 1}], "connectors": {},
    "workflowMetaData": {
        "applicationBundleIDsByPath": {}, "applicationPaths": [],
        "inputTypeIdentifier": "com.apple.Automator.fileSystemObject",
        "outputTypeIdentifier": "com.apple.Automator.nothing",
        "presentationMode": 15, "processesInput": False,
        "serviceInputTypeIdentifier": "com.apple.Automator.fileSystemObject",
        "serviceOutputTypeIdentifier": "com.apple.Automator.nothing",
        "serviceProcessesInput": False, "systemImageName": "NSActionTemplate",
        "useAutomaticInputType": False,
        "workflowTypeIdentifier": "com.apple.Automator.servicesMenu",
    },
}
plistlib.dump(info, open(f"{dest}/Info.plist", "wb"))
plistlib.dump(doc, open(f"{dest}/document.wflow", "wb"))
print("Quick Action installed:", os.path.dirname(dest))
