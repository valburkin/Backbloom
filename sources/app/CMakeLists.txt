# generate windows .rc file for this binary
if (WIN32)
	include(win_rc)
	generate_win_rc_file(overglow)
endif()

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
if (WIN32)
	target_link_options(overglow PUBLIC /SUBSYSTEM:WINDOWS /ENTRY:mainCRTStartup)
endif()

target_link_libraries(overglow
	blackborder
	commandline
	database
	effects
	api
	base
	utils
	hyperimage
	image
	jsonserver
	json-utils
	led-drivers
	lut-calibrator
	performance-counters
	resources
	infinite-engine
	ssdp
	suspend-handler
	utils-image
	webserver
	${APPKIT_FRAMEWORK}
)

if((ENABLE_POWER_MANAGEMENT OR ENABLE_SYSTRAY) AND UNIX AND NOT APPLE)
	target_link_libraries(overglow micro-dbus)
endif()

if (WIN32 OR APPLE)
	target_link_libraries(overglow systray-widget)
else()
	install(TARGETS systray-widget
		LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
		PUBLIC_HEADER DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
endif()

if(ENABLE_ZSTD)
	target_link_libraries(overglow zstd::zstd utils-zstd)
endif()

if (USE_STATIC_QT_PLUGINS)
	target_link_libraries(overglow ${STATIC_QT_PLUGINS_LIBS})
endif()

if (ENABLE_MQTT)
	target_link_libraries(overglow mqtt)
endif ()

if (ENABLE_BONJOUR)
	target_link_libraries(overglow bonjour)
endif ()

if (ENABLE_V4L2)
	target_link_libraries(overglow v4l2-grabber)
endif ()

if (ENABLE_MF)
	target_link_libraries(overglow grabber-MF)
endif ()

if (ENABLE_DX)
	target_link_libraries(overglow grabber-DX)
endif ()

if (ENABLE_X11)
	target_link_libraries(overglow X11-grabber "${CMAKE_DL_LIBS}")
endif ()

if (ENABLE_PIPEWIRE)
	target_link_libraries(overglow Pipewire-grabber "${CMAKE_DL_LIBS}")
endif ()

if (ENABLE_MAC_SYSTEM)
	target_link_libraries(overglow
		MACOS-grabber
		"-framework CoreGraphics"
		"-framework Foundation")
	if (MACOS_SCK)
		target_link_libraries(overglow "${MACOS_SCK}")
	endif(MACOS_SCK)
endif()

if (ENABLE_AVF)
	target_link_libraries(overglow 
		AVF-grabber
		"-framework CoreGraphics"
		"-framework AVFoundation"
		"-framework Foundation"
		"-framework CoreMedia"
		"-framework CoreVideo"
		"-framework Accelerate"
		"-framework IOKit")
endif ()

if (ENABLE_SOUNDCAPWINDOWS)
	target_link_libraries(overglow sound-capture-windows)
endif ()

if (ENABLE_SOUNDCAPLINUX)
	target_link_libraries(overglow sound-capture-linux)
endif ()

if (ENABLE_SOUNDCAPMACOS)
	target_link_libraries(overglow sound-capture-macos)
endif ()

# rules for installers
if (APPLE) # macOS
	include(${OUR_CMAKE_MODULES}/../installer_macos.cmake)
	InstallerMacOS("overglow")
elseif (NOT WIN32) # Linux

	if(ENABLE_DEPENDENCY_PACKAGING)
		set(rpath_libs_dir "$ORIGIN/../${CMAKE_INSTALL_LIBDIR}/overglow:$ORIGIN/../${CMAKE_INSTALL_LIBDIR}/overglow/external:$ORIGIN/../${CMAKE_INSTALL_LIBDIR}/overglow/external/plugins")
	else()
		set(rpath_libs_dir "$ORIGIN/../${CMAKE_INSTALL_LIBDIR}/overglow")
	endif()

	set_target_properties(overglow PROPERTIES
		INSTALL_RPATH       "${rpath_libs_dir}"
		BUILD_WITH_INSTALL_RPATH FALSE
		INSTALL_RPATH_USE_LINK_PATH FALSE)

	include(${OUR_CMAKE_MODULES}/../installer_linux.cmake)
	InstallerLinux("overglow")
elseif(WIN32) # Windows
	include(${OUR_CMAKE_MODULES}/../installer_windows.cmake)
	InstallerWindows("overglow")
endif ()

if(USE_PRECOMPILED_HEADERS AND COMMAND target_precompile_headers AND WIN32)
    target_precompile_headers(overglow REUSE_FROM precompiled_headers)
endif()
