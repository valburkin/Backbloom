# Define the current source locations

SET(CURRENT_HEADER_DIR ${CMAKE_SOURCE_DIR}/include/performance-counters)
SET(CURRENT_SOURCE_DIR ${CMAKE_SOURCE_DIR}/sources/performance-counters)

FILE ( GLOB_RECURSE performance_counters_SOURCES "${CURRENT_HEADER_DIR}/*.h"  "${CURRENT_SOURCE_DIR}/*.h"  "${CURRENT_SOURCE_DIR}/*.cpp" )

add_library(performance-counters OBJECT ${performance_counters_SOURCES})

target_link_libraries(performance-counters
	Qt6::Core
	Qt6::Network
)

if(USE_PRECOMPILED_HEADERS AND COMMAND target_precompile_headers)
	target_precompile_headers(performance-counters REUSE_FROM precompiled_headers)
endif()
