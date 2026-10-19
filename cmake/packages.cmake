# Windows packages: NSIS installer, plus a ZIP archive with BUILD_ARCHIVES
if(BUILD_ARCHIVES)
	SET ( CPACK_GENERATOR "ZIP" "NSIS")
ELSE()
	SET ( CPACK_GENERATOR "NSIS")
ENDIF()

# Apply to all packages, some of these can be overwritten with generator specific content
# https://cmake.org/cmake/help/v3.5/module/CPack.html

SET ( CPACK_PACKAGE_NAME "Overglow" )
SET ( CPACK_PACKAGE_DESCRIPTION_SUMMARY "Overglow ambient lighting for TVs and monitors" )
SET ( CPACK_PACKAGE_DESCRIPTION_FILE "${CMAKE_SOURCE_DIR}/README.md" )
SET ( CPACK_PACKAGE_FILE_NAME "Overglow-${OVERGLOW_VERSION}-${CMAKE_SYSTEM_NAME}-${CMAKE_SYSTEM_PROCESSOR}")

SET ( CPACK_PACKAGE_CONTACT "Overglow")
SET ( CPACK_PACKAGE_VENDOR "Overglow")
SET ( CPACK_PACKAGE_EXECUTABLES "overglow;Overglow" )
SET ( CPACK_PACKAGE_INSTALL_DIRECTORY "Overglow" )
SET ( CPACK_PACKAGE_ICON "${CMAKE_SOURCE_DIR}/branding/png/overglow-32.png")

SET ( CPACK_PACKAGE_VERSION_MAJOR "${OVERGLOW_VERSION_MAJOR}")
SET ( CPACK_PACKAGE_VERSION_MINOR "${OVERGLOW_VERSION_MINOR}")
SET ( CPACK_PACKAGE_VERSION_PATCH "${OVERGLOW_VERSION_PATCH}")

SET ( CPACK_RESOURCE_FILE_LICENSE "${CMAKE_SOURCE_DIR}/LICENSE" )

SET ( CPACK_CREATE_DESKTOP_LINKS "overglow;Overglow" )
SET ( CPACK_ARCHIVE_THREADS 0 )

# Windows NSIS
# Use custom script based on cpack nsis template
set(CMAKE_MODULE_PATH ${PROJECT_SOURCE_DIR}/cmake/windows/template ${CMAKE_MODULE_PATH})
# Some path transformations
file(TO_NATIVE_PATH ${CPACK_PACKAGE_ICON} CPACK_PACKAGE_ICON)
STRING(REGEX REPLACE "\\\\" "\\\\\\\\" CPACK_PACKAGE_ICON ${CPACK_PACKAGE_ICON})
file(TO_NATIVE_PATH "${CMAKE_SOURCE_DIR}/branding/overglow.ico" NSIS_HYP_ICO)
file(TO_NATIVE_PATH "${CMAKE_SOURCE_DIR}/branding/installer/header.bmp" NSIS_HYP_LOGO_HORI)
file(TO_NATIVE_PATH "${CMAKE_SOURCE_DIR}/branding/installer/welcome.bmp" NSIS_HYP_LOGO_VERT)
STRING(REGEX REPLACE "\\\\" "\\\\\\\\" NSIS_HYP_ICO "${NSIS_HYP_ICO}")
STRING(REGEX REPLACE "\\\\" "\\\\\\\\" NSIS_HYP_LOGO_VERT "${NSIS_HYP_LOGO_VERT}")
STRING(REGEX REPLACE "\\\\" "\\\\\\\\" NSIS_HYP_LOGO_HORI "${NSIS_HYP_LOGO_HORI}")

SET ( CPACK_NSIS_MODIFY_PATH ON )
if(NOT BUILD_ARCHIVES)
	SET ( CPACK_NSIS_COMPRESSOR "" )
else()
	SET ( CPACK_NSIS_COMPRESSOR "/SOLID lzma" )
endif()
SET ( CPACK_NSIS_MUI_ICON ${NSIS_HYP_ICO})
SET ( CPACK_NSIS_MUI_UNIICON ${NSIS_HYP_ICO})
SET ( CPACK_NSIS_MUI_HEADERIMAGE ${NSIS_HYP_LOGO_HORI} )
SET ( CPACK_NSIS_MUI_WELCOMEFINISHPAGE_BITMAP ${NSIS_HYP_LOGO_VERT})
SET ( CPACK_NSIS_DISPLAY_NAME "Overglow Ambient Light")
SET ( CPACK_NSIS_PACKAGE_NAME "Overglow" )
SET ( CPACK_NSIS_INSTALLED_ICON_NAME "bin\\\\overglow.exe")
if (OVERGLOW_SUPPORT_URL)
	SET ( CPACK_NSIS_HELP_LINK "${OVERGLOW_SUPPORT_URL}")
	SET ( CPACK_NSIS_URL_INFO_ABOUT "${OVERGLOW_SUPPORT_URL}")
endif()
SET ( CPACK_NSIS_MUI_FINISHPAGE_RUN "overglow.exe")
SET ( CPACK_NSIS_BRANDING_TEXT "Overglow-${OVERGLOW_VERSION}")
# custom nsis plugin directory
SET ( CPACK_NSIS_EXTRA_DEFS "!addplugindir ${CMAKE_SOURCE_DIR}/cmake/windows/plugins")
# additional overglow startmenu link, won't be created if the user disables startmenu links
SET ( CPACK_NSIS_CREATE_ICONS_EXTRA "CreateShortCut '$SMPROGRAMS\\\\$STARTMENU_FOLDER\\\\Overglow (Console).lnk' '$INSTDIR\\\\bin\\\\overglow.exe' '-d -c'")
SET ( CPACK_NSIS_DELETE_ICONS_EXTRA "Delete '$SMPROGRAMS\\\\$MUI_TEMP\\\\Overglow (Console).lnk'")




# define the install components
# See also https://gitlab.kitware.com/cmake/community/-/wikis/doc/cpack/Component-Install-With-CPack
# and https://cmake.org/cmake/help/latest/module/CPackComponent.html
SET ( CPACK_COMPONENTS_GROUPING "ALL_COMPONENTS_IN_ONE")
# Components base
SET ( CPACK_COMPONENTS_ALL "Overglow" )

SET ( CPACK_ARCHIVE_COMPONENT_INSTALL ON )

SET ( CPACK_STRIP_FILES ON )

# no code after following line!
INCLUDE ( CPack )

cpack_add_install_type(Full DISPLAY_NAME "Full")
cpack_add_install_type(Min DISPLAY_NAME "Minimal")
cpack_add_component_group(Runtime EXPANDED DESCRIPTION "Overglow runtime")

# Components base
cpack_add_component(Overglow
	DISPLAY_NAME "Overglow"
	DESCRIPTION "Overglow runtime"
	INSTALL_TYPES Full Min
	GROUP Runtime
	REQUIRED
)

