if (Test-Path -Path "./bin") {
    Remove-Item ./bin -recurse
}
dotnet publish -c Release -p:CreateCipx=true