Import-Module AWSPowerShell -Force -ErrorAction Stop
Framework 4.5.1

function Write-Documentation(){
	write-host "Example - package - release: psake package -properties @{release='1.1.1.1'}"
    write-host "Example - delete tag: psake DeleteTag -properties @{Tag='2016.01.*'} - * => Wildcard"
}

function Check-ProductionTag($tag){

    #need to figure out how to check if end has "_old" or "_orig"
    $regexProdTags = "^\d{1}\.\d{1}\.\d{1}\.\d{1,2}$"

    if($item -notmatch $regexProdTags -and $item -ne "v1.0.0.0"){
        #Write-Host $item " " $item.Length " " $item.Substring($item.Length-4, 4).ToLower() 
        if($item.Length -gt 5 -and $item.Substring($item.Length-4, 4).ToLower() -ne "_old" -and $item.Substring($item.Length-5, 5).ToLower() -ne "_orig"){
            return $false;
        }
    }

    return $true;
}

task default -depends ?

properties{
    $release = $release
    $Tag = $Tag
    $DaysToKeepTag = $DaysToKeepTag

    $build_dir = Split-Path $psake.build_script_file
    $build_artifacts_dir = "$build_dir\..\BuildArtifacts\"
    $solution_path = "$build_dir\src\Harbour.RedisSessionStateStore.sln"
    $solution_dir = "$build_dir\src\"
    
    #$configFile = Get-Content -Raw -Path ".\config.json" | ConvertFrom-Json
}

task ? -Description "Display Help" {
	Write-Documentation
}

task verify_variables {
    Assert ($release -ne $null) "psake variable does not contain 'release'"
}

task DeleteTag -depends ? {

    $foundTags = Exec { git tag -l $Tag }

    ForEach($item In $foundTags){

        if(-Not (Check-ProductionTag($item))){
            Write-Host $item
            Exec { git tag -d $item }
            Exec { git push origin :refs/tags/$item -q }
        } else{
            Write-Host "Not Deleting: " $item
        }
    }
}

task updateAssemblyInfo -depends verify_variables {
    Update-AssemblyInfoFiles -version $release
}

task tagRelease -depends verify_variables{
    exec {git fetch --tags} 
    exec {git tag -a "$release" -m "Release"} 
    exec {git push origin $release}
}

task build {
	Exec { msbuild "$solution_path" /t:Build /p:Configuration=Release /v:quiet }
}

task package -depends updateAssemblyInfo, build, tagRelease {
	$specPath = "$build_dir\build\Harbour.RedisSessionStateStore.nuspec"
    $specFileName = "Harbour.RedisSessionStateStore.nuspec"
	
	#Update the version information
    $Spec = [xml](get-content "$specPath")
    $Spec.package.metadata.version = ([string]$Spec.package.metadata.version).Replace("{Version}",$release)
    $Spec.Save("$build_dir\$specFileName")

    exec { nuget pack "$specPath" -OutputDirectory $build_dir }
    
    $NuGetPackageName = "PHTech.Harbour.RedisSessionStateStore.$release.nupkg"
    exec { nuget delete "PHTech.Harbour.RedisSessionStateStore" $release 098b2149-1570-4fbb-bcef-88554c812752 -Source https://www.myget.org/F/phtech/api/v2/package -NonInteractive }
    exec { nuget push "$build_dir\$NuGetPackageName" 098b2149-1570-4fbb-bcef-88554c812752 -Source https://www.myget.org/F/phtech-pdt/api/v2/package }
    
	Update-AssemblyInfoFiles ("1.0.0.0")
	
	
}

function Update-AssemblyInfoFiles ([string] $version, [System.Array] $excludes = $null, $make_writeable = $false) {

#-------------------------------------------------------------------------------
# Update version numbers of AssemblyInfo.cs
# adapted from: http://www.luisrocha.net/2009/11/setting-assembly-version-with-windows.html
# https://gist.github.com/toddb/1133511
#-------------------------------------------------------------------------------

	if ($version -notmatch "[0-9]+(\.([0-9]+|\*)){1,3}") {
		Write-Error "Version number incorrect format: $version"
	}
	
	$versionPattern = 'AssemblyVersion\("[0-9]+(\.([0-9]+|\*)){1,3}"\)'
	$versionAssembly = 'AssemblyVersion("' + $version + '")';
	$versionFilePattern = 'AssemblyFileVersion\("[0-9]+(\.([0-9]+|\*)){1,3}"\)'
	$versionAssemblyFile = 'AssemblyFileVersion("' + $version + '")';

	Get-ChildItem -r -filter AssemblyInfo.cs -ErrorAction SilentlyContinue | % {
		$filename = $_.fullname
		
		$update_assembly_and_file = $true
		
		# set an exclude flag where only AssemblyFileVersion is set
		if ($excludes -ne $null)
			{ $excludes | % { if ($filename -match $_) { $update_assembly_and_file = $false	} } }
		

		# We are using a source control (TFS) that requires to check-out files before 
		# modifying them. We don't want checkins so we'll just toggle
		# the file as writeable/readable	
	
		if ($make_writable) { Writeable-AssemblyInfoFile($filename) }

		# see http://stackoverflow.com/questions/3057673/powershell-locking-file
		# I am getting really funky locking issues. 
		# The code block below should be:
		#     (get-content $filename) | % {$_ -replace $versionPattern, $version } | set-content $filename

		$tmp = ($file + ".tmp")
		if (test-path ($tmp)) { remove-item $tmp }

		if ($update_assembly_and_file) {
			(get-content $filename) | % {$_ -replace $versionFilePattern, $versionAssemblyFile } | % {$_ -replace $versionPattern, $versionAssembly }  > $tmp
			write-host Updating file AssemblyInfo and AssemblyFileInfo: $filename --> $versionAssembly / $versionAssemblyFile
		} else {
			(get-content $filename) | % {$_ -replace $versionFilePattern, $versionAssemblyFile } > $tmp
			write-host Updating file AssemblyInfo only: $filename --> $versionAssemblyFile
		}

		if (test-path ($filename)) { remove-item $filename }
		move-item $tmp $filename -force	

		if ($make_writable) { ReadOnly-AssemblyInfoFile($filename) }		

	}
}