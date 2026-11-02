const statistics = document.getElementById('library-statistics');
if (statistics && typeof Chart !== 'undefined') {
    const data = JSON.parse(statistics.textContent);
    const colours = ['#c6f568', '#76b7ff', '#a995ff', '#ffcb75', '#f58f9e', '#62d4bc'];
    Chart.defaults.color = '#969eaf';
    Chart.defaults.font.family = 'Inter, "Segoe UI", sans-serif';
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const makeChart = (id, values, type, extra = {}) => {
        const canvas = document.getElementById(id);
        if (!canvas) return;
        new Chart(canvas, {
            type,
            data: { labels: values.map(value => value.label), datasets: [{ label: 'Games', data: values.map(value => value.count), backgroundColor: colours, borderWidth: 0, borderRadius: type === 'bar' ? 5 : 0 }] },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: reducedMotion ? false : { duration: 400 },
                plugins: { legend: { display: type === 'doughnut', position: 'bottom', labels: { color: '#eff1f6', boxWidth: 12, padding: 16 } } },
                ...extra
            }
        });
    };
    const countAxis = { beginAtZero: true, ticks: { precision: 0 }, grid: { color: '#252a36' } };
    makeChart('status-chart', data.statuses, 'doughnut', { cutout: '68%' });
    makeChart('genre-chart', data.genres, 'bar', { indexAxis: 'y', scales: { x: countAxis, y: { grid: { display: false } } } });
    makeChart('completion-chart', data.months, 'bar', { scales: { y: countAxis, x: { grid: { display: false } } } });
}
