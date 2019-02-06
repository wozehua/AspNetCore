function Test-Template($templateName, $templateArgs, $templateNupkg, $isSPA) {
    $tmpDir = "$PSScriptRoot/$templateName"
    Remove-Item -Path $tmpDir -Recurse -ErrorAction Ignore
    #dotnet pack

    Run-DotnetNew "--install", "$PSScriptRoot/../../../artifacts/packages/Debug/Shipping/$templateNupkg"

    New-Item -ErrorAction Ignore -Path $tmpDir -ItemType Directory
    Push-Location $tmpDir
    try {
        Run-DotnetNew $templateArgs, "--no-restore"

        if ($templateArgs -match 'F#') {
            $extension = "fsproj"
        }
        else {
            $extension = "csproj"
        }

        $proj = "$tmpDir/$templateName.$extension"
        if ($templateName -eq "razorcomponents")
        {
            $proj = "$tmpDir/$templateName.Server/$templateName.Server.$extension"
        }

        $dirBuildProps = "$tmpDir/Directory.Build.props"
        $dirBuildTargets = "$tmpDir/Directory.Build.targets"
        New-Item -Path $dirBuildProps
        New-Item -Path $dirBuildTargets
        $dirContent = '<Project/>'
        $dirContent | Set-Content $dirBuildProps
        $dirContent | Set-Content $dirBuildTargets

        $projContent = Get-Content -Path $proj -Raw
        $projContent = $projContent -replace ('<Project Sdk="Microsoft.NET.Sdk.Web">', "<Project Sdk=""Microsoft.NET.Sdk.Web"">
  <Import Project=""$PSScriptRoot/../test/bin/Debug/netcoreapp3.0/TemplateTests.props"" />
  <ItemGroup>
    <PackageReference Include=""Microsoft.NET.Sdk.Razor"" Version=""`$(MicrosoftNETSdkRazorPackageVersion)"" />
  </ItemGroup>")
        $projContent | Set-Content $proj

        if ($templateName -eq "razorcomponents")
        {
            Push-Location "razorcomponents.Server"
            $loc = Get-Location
            Write-Output "LOCATIOND $loc"
        }

        dotnet ef migrations add mvc

        dotnet publish --configuration Release
        dotnet bin\Release\netcoreapp3.0\publish\$templateName.dll

        if($templateName -eq "razorcomponents")
        {
            Pop-Location
        }
    }
    finally {
        Pop-Location
        Run-DotnetNew "--debug:reinit"
    }
}

function Run-DotnetNew($arguments) {
    $expression = "dotnet new $arguments"
    Invoke-Expression $expression
}
