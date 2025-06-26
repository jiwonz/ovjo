xgettext -d ovjo -o Ovjo/locales/ovjo.pot --from-code UTF-8 --keyword=_:1 (Get-ChildItem -Recurse -Filter *.cs | ForEach-Object { $_.FullName })
