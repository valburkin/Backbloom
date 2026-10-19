# Define the current source locations

SET(CURRENT_HEADER_DIR ${CMAKE_SOURCE_DIR}/include/utils)
SET(CURRENT_SOURCE_DIR ${CMAKE_SOURCE_DIR}/sources/utils)

FILE ( GLOB_RECURSE Utils_SOURCES "${CURRENT_HEADER_DIR}/*.h"  "${CURRENT_SOURCE_DIR}/*.h"  "${CURRENT_SOURCE_DIR}/*.cpp" )

add_library(utils OBJECT ${Utils_SOURCES})

target_link_libraries(utils
	Qt6::Core
	Qt6::Network
	linalg
)

if(USE_PRECOMPILED_HEADERS AND COMMAND target_precompile_headers)
    target_precompile_headers(utils REUSE_FROM precompiled_headers)
endif()
