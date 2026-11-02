const gamePicker = $("#collection-game");
const searchNotice = $("#collection-search-notice");

gamePicker.select2({
    width: "100%",
    placeholder: "Search games, e.g. Elden Ring...",
    minimumInputLength: 3,
    maximumInputLength: 100,
    ajax: {
        url: gamePicker.data("search-url"),
        dataType: "json",
        delay: 350,
        data: params => ({ term: params.term.trim(), page: params.page || 1 }),
        processResults: data => {
            searchNotice.text(data.notice || "").prop("hidden", !data.notice);
            return data;
        }
    }
});
