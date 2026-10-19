# generate windows .rc file for this binary
include(win_rc)
generate_win_rc_file(overglow)

add_executable(overglow
	AppDaemon.h
	LegacyMigration.h
	SystrayHandler.h
	AppDaemon.cpp
	LegacyMigration.cpp
	SystrayHandler.cpp
	main.cpp
	${overglow_WIN_RC_PATH}
)

# promote overglow as GUI app
target_link_options(overglow PUBLIC /SUBSYSTEM:WINDOWS /ENTRY:mainCRTStartup)

target_link_libraries(overglow
	blackborder
	bonjour
	commandline
	database
	effects
	api
	base
	utils
	hyperimage
	image
	json-utils
	led-drivers
	mqtt
	performance-counters
	resources
	infinite-engine
	ssdp
	suspend-handler
	systray-widget
	utils-image
	webserver
	grabber-DX
	sound-capture-windows
	zstd::zstd
	utils-zstd
)

# rules for the installer
include(${OUR_CMAKE_MODULES}/../installer_windows.cmake)
InstallerWindows("overglow")

if(USE_PRECOMPILED_HEADERS AND COMMAND target_precompile_headers)
    target_precompile_headers(overglow REUSE_FROM precompiled_headers)
endif()
