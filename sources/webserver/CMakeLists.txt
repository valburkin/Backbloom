# Define the current source locations
set(CURRENT_HEADER_DIR ${CMAKE_SOURCE_DIR}/include/webserver)
set(CURRENT_SOURCE_DIR ${CMAKE_SOURCE_DIR}/sources/webserver)

FILE ( GLOB WebConfig_SOURCES "${CURRENT_HEADER_DIR}/*.h"  "${CURRENT_SOURCE_DIR}/*.h"  "${CURRENT_SOURCE_DIR}/*.cpp" )
FILE ( GLOB_RECURSE webFiles RELATIVE ${CMAKE_BINARY_DIR}  ${CMAKE_SOURCE_DIR}/www/* )

FOREACH( f ${webFiles} )
    STRING ( REPLACE "www/" ";" workingWebFile ${f})
    list(GET workingWebFile -1 fname)
    SET(WEBCONFIG_RESOURCE_ENTRIES "${WEBCONFIG_RESOURCE_ENTRIES}\n\t\t<file alias=\"/www/${fname}\">${f}</file>")
ENDFOREACH()
# legal notices, shown by the About page of the web UI
set(LEGAL_NOTICES ${CMAKE_SOURCE_DIR}/LICENSE ${CMAKE_SOURCE_DIR}/3RD_PARTY_LICENSES)
FOREACH( notice ${LEGAL_NOTICES} )
    file(RELATIVE_PATH noticePath ${CMAKE_BINARY_DIR} ${notice})
    get_filename_component(noticeName ${notice} NAME)
    SET(WEBCONFIG_RESOURCE_ENTRIES "${WEBCONFIG_RESOURCE_ENTRIES}\n\t\t<file alias=\"/www/legal/${noticeName}.txt\">${noticePath}</file>")
ENDFOREACH()
CONFIGURE_FILE(${CURRENT_SOURCE_DIR}/web_resources.qrc.in ${CMAKE_BINARY_DIR}/web_resources.qrc )

if (USE_EMBEDDED_WEB_RESOURCES)
    qt_add_resources(WebConfig_RESOURCES ${CMAKE_BINARY_DIR}/web_resources.qrc)
endif()

add_library(webserver OBJECT
	${WebConfig_SOURCES}
	${WebConfig_RESOURCES}
)

if (NOT USE_EMBEDDED_WEB_RESOURCES)
    if (WIN32)
        set(EMBEDDED_WEB_RESOURCES ${CMAKE_BINARY_DIR}/bin/lib/web_resources.rcc)
    else()
        set(EMBEDDED_WEB_RESOURCES ${CMAKE_BINARY_DIR}/lib/web_resources.rcc)
    endif()

    FILE ( GLOB_RECURSE web_resources_list ${CMAKE_SOURCE_DIR}/www/* )
    list(APPEND web_resources_list ${LEGAL_NOTICES})

    add_custom_command (
        OUTPUT ${EMBEDDED_WEB_RESOURCES}
        COMMAND Qt${Qt_VERSION}::rcc -no-compress --binary ${CMAKE_BINARY_DIR}/web_resources.qrc -o ${EMBEDDED_WEB_RESOURCES}
        MAIN_DEPENDENCY ${CMAKE_BINARY_DIR}/web_resources.qrc
        DEPENDS ${web_resources_list}
        VERBATIM)

    add_custom_target( webserver-resources DEPENDS ${EMBEDDED_WEB_RESOURCES} DEPENDS ${web_resources_list} )
    set_target_properties( webserver-resources PROPERTIES OUTPUT_FILE ${EMBEDDED_WEB_RESOURCES} )
    add_dependencies( webserver webserver-resources )
endif()

target_link_libraries(webserver
	Qt${Qt_VERSION}::Core
	Qt${Qt_VERSION}::Network
)

if (USE_STATIC_QT_PLUGINS)
	target_link_libraries(webserver ${STATIC_QT_PLUGINS_LIBS})
endif()

if(USE_PRECOMPILED_HEADERS AND COMMAND target_precompile_headers)
	target_precompile_headers(webserver REUSE_FROM precompiled_headers)
endif()
